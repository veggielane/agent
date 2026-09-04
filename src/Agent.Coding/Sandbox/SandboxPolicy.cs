using System.Globalization;
using System.Text.RegularExpressions;

namespace Agent.Coding.Sandbox;

/// <summary>
/// Decides the container settings for a run from the host configuration plus the repository's request.
/// The rule everywhere is that a repository may narrow but never widen: it picks from the host's profile
/// menu, or names an image the host allow-lists; it may lower memory and CPU but not raise them past the
/// host ceiling; it may close the network but not open it. Violations throw, so a task fails with a clear
/// message instead of quietly running somewhere other than the repository asked for.
/// </summary>
public static partial class SandboxPolicy
{
    public static ResolvedSandbox Resolve(SandboxOptions host, RepoProfile repo)
    {
        var request = repo.Container ?? RepoContainer.Empty;

        var image = host.DefaultImage;
        var network = host.Network;
        var memory = host.Memory;
        var cpus = host.Cpus;
        var volumes = new List<string>(host.Volumes.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim()));
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string source;

        if (!string.IsNullOrWhiteSpace(request.Profile))
        {
            var name = request.Profile.Trim();
            var profile = Find(host.Profiles, name)
                ?? throw new SandboxException(
                    $"The repository asked for container profile '{name}', which this agent does not define. " +
                    (host.Profiles.Count == 0
                        ? "No profiles are configured; ask an administrator to add one, or remove the profile from .engex.yml."
                        : $"Available profiles: {string.Join(", ", host.Profiles.Keys.Order(StringComparer.OrdinalIgnoreCase))}."));

            if (string.IsNullOrWhiteSpace(profile.Image))
            {
                throw new SandboxException($"Container profile '{name}' is configured without an image.");
            }

            image = profile.Image.Trim();
            network = string.IsNullOrWhiteSpace(profile.Network) ? network : profile.Network.Trim();
            memory = string.IsNullOrWhiteSpace(profile.Memory) ? memory : profile.Memory.Trim();
            cpus = profile.Cpus ?? cpus;
            volumes.AddRange(profile.Volumes.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim()));
            foreach (var (k, v) in profile.Env)
            {
                env[k] = v;
            }

            source = $"profile '{name}'";
        }
        else if (!string.IsNullOrWhiteSpace(request.Image))
        {
            var requested = request.Image.Trim();
            var allowed = host.AllowedImages.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()).ToList();

            if (allowed.Count == 0)
            {
                throw new SandboxException(
                    $"The repository asked for image '{requested}', but this agent does not allow repositories to name images. " +
                    (host.Profiles.Count == 0
                        ? "Ask an administrator to allow-list the image."
                        : $"Use one of the container profiles instead: {string.Join(", ", host.Profiles.Keys.Order(StringComparer.OrdinalIgnoreCase))}."));
            }

            if (!allowed.Any(pattern => MatchesGlob(requested, pattern)))
            {
                throw new SandboxException(
                    $"The repository asked for image '{requested}', which is not allow-listed. Allowed patterns: {string.Join(", ", allowed)}.");
            }

            image = requested;
            source = "repository image";
        }
        else
        {
            image = DockerSandbox.ResolveImage(host, repo);
            var toolchain = DockerSandbox.ToolchainOf(repo);
            source = toolchain is not null && host.Images.ContainsKey(toolchain) ? $"toolchain '{toolchain}'" : "default image";
        }

        // Memory and CPU are resource requests: honour them downward, clamp them at the host ceiling.
        if (!string.IsNullOrWhiteSpace(request.Memory))
        {
            memory = ClampMemory(request.Memory.Trim(), memory, host.MaxMemory);
        }

        if (request.Cpus is { } requestedCpus)
        {
            if (requestedCpus <= 0)
            {
                throw new SandboxException($"The repository asked for {requestedCpus} CPUs, which is not a positive number.");
            }

            cpus = host.MaxCpus > 0 ? Math.Min(requestedCpus, host.MaxCpus) : requestedCpus;
        }

        // The network may only be closed further, never opened.
        if (!string.IsNullOrWhiteSpace(request.Network))
        {
            var requestedNetwork = request.Network.Trim();
            if (string.Equals(requestedNetwork, "none", StringComparison.OrdinalIgnoreCase))
            {
                network = "none";
            }
            else if (!string.Equals(requestedNetwork, network, StringComparison.OrdinalIgnoreCase))
            {
                throw new SandboxException(
                    $"The repository asked for network '{requestedNetwork}', but a repository may only close the network ('none'), not change it. This agent uses '{network}'.");
            }
        }

        // Repository environment is for build knobs; the host's own values win on conflict.
        if (request.Env is not null)
        {
            foreach (var (key, value) in request.Env)
            {
                if (!string.IsNullOrWhiteSpace(key))
                {
                    env[key.Trim()] = value ?? string.Empty;
                }
            }
        }

        foreach (var (key, value) in host.Env)
        {
            if (!string.IsNullOrWhiteSpace(key))
            {
                env[key.Trim()] = value ?? string.Empty;
            }
        }

        return new ResolvedSandbox(image, network, memory, cpus, volumes, env, source);
    }

    /// <summary>Clamps a requested memory limit to the host ceiling, keeping the requested string when it is smaller.</summary>
    public static string ClampMemory(string requested, string hostValue, string? ceiling)
    {
        if (!TryParseMemory(requested, out var requestedBytes))
        {
            throw new SandboxException($"The repository asked for memory '{requested}', which is not a size like 512m, 4g or 2048.");
        }

        if (!string.IsNullOrWhiteSpace(ceiling) && TryParseMemory(ceiling, out var ceilingBytes) && requestedBytes > ceilingBytes)
        {
            return ceiling.Trim();
        }

        return requested;
    }

    /// <summary>Parses docker's memory syntax: a number with an optional b/k/m/g suffix.</summary>
    public static bool TryParseMemory(string value, out long bytes)
    {
        bytes = 0;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var text = value.Trim();
        var multiplier = 1L;
        var lastChar = char.ToLowerInvariant(text[^1]);

        if (lastChar is 'b' or 'k' or 'm' or 'g')
        {
            multiplier = lastChar switch
            {
                'k' => 1024L,
                'm' => 1024L * 1024,
                'g' => 1024L * 1024 * 1024,
                _ => 1L,
            };
            text = text[..^1];
        }

        if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) || number < 0)
        {
            return false;
        }

        bytes = number * multiplier;
        return true;
    }

    /// <summary>Case-insensitive glob match supporting <c>*</c> and <c>?</c>, used for image allow-lists.</summary>
    public static bool MatchesGlob(string value, string pattern)
    {
        var regex = "^" + string.Join(".*", pattern.Split('*').Select(part => string.Join(".", part.Split('?').Select(Regex.Escape)))) + "$";
        return Regex.IsMatch(value, regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    }

    private static SandboxProfile? Find(IDictionary<string, SandboxProfile> profiles, string name)
    {
        foreach (var (key, profile) in profiles)
        {
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
            {
                return profile;
            }
        }

        return null;
    }
}
