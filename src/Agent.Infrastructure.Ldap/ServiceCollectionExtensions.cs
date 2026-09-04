using Agent.Core.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Agent.Infrastructure.Ldap;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Direct LDAP group lookups. The provider is registered whether or not <c>Ldap:Enabled</c> is set;
    /// Core picks it when <c>Authorization:Provider</c> is "Ldap".
    /// </summary>
    public static IServiceCollection AddLdapAuthorization(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<LdapOptions>()
            .Bind(configuration.GetSection(LdapOptions.SectionName))
            .ValidateDataAnnotations();

        services.TryAddSingleton<ILdapSearcher, LdapConnectionSearcher>();
        services.AddSingleton<IGroupMembershipProvider, LdapGroupMembershipProvider>();
        return services;
    }
}
