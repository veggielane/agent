using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agent.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Audit",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Timestamp = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Channel = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    CallerId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    CallerName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    Action = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Outcome = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Detail = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Roles = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Audit", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ChannelCursors",
                columns: table => new
                {
                    Key = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Value = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChannelCursors", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "ProcessedEvents",
                columns: table => new
                {
                    Channel = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    EventId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    ProcessedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProcessedEvents", x => new { x.Channel, x.EventId });
                });

            migrationBuilder.CreateTable(
                name: "TaskEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TaskId = table.Column<int>(type: "int", nullable: false),
                    At = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Type = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Message = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Tasks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Source = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    SourceRef = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    SourceUrl = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: true),
                    Title = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    RequesterId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    RequesterName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    RequesterUsername = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    NotifyChannel = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ConversationId = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    RepoUrl = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: true),
                    ProjectId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    BaseBranch = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    WorkBranch = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    MergeRequestUrl = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: true),
                    MergeRequestIid = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Instruction = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    PendingInstruction = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Summary = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Error = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Turns = table.Column<int>(type: "int", nullable: false),
                    TokensUsed = table.Column<long>(type: "bigint", nullable: false),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    WorkerId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    MetadataJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tasks", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Audit_Timestamp",
                table: "Audit",
                column: "Timestamp");

            migrationBuilder.CreateIndex(
                name: "IX_ProcessedEvents_ProcessedAt",
                table: "ProcessedEvents",
                column: "ProcessedAt");

            migrationBuilder.CreateIndex(
                name: "IX_TaskEvents_At",
                table: "TaskEvents",
                column: "At");

            migrationBuilder.CreateIndex(
                name: "IX_TaskEvents_TaskId",
                table: "TaskEvents",
                column: "TaskId");

            migrationBuilder.CreateIndex(
                name: "IX_Tasks_ProjectId_MergeRequestIid",
                table: "Tasks",
                columns: new[] { "ProjectId", "MergeRequestIid" });

            migrationBuilder.CreateIndex(
                name: "IX_Tasks_RequesterId",
                table: "Tasks",
                column: "RequesterId");

            migrationBuilder.CreateIndex(
                name: "IX_Tasks_Source_SourceRef",
                table: "Tasks",
                columns: new[] { "Source", "SourceRef" });

            migrationBuilder.CreateIndex(
                name: "IX_Tasks_Status",
                table: "Tasks",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Audit");

            migrationBuilder.DropTable(
                name: "ChannelCursors");

            migrationBuilder.DropTable(
                name: "ProcessedEvents");

            migrationBuilder.DropTable(
                name: "TaskEvents");

            migrationBuilder.DropTable(
                name: "Tasks");
        }
    }
}
