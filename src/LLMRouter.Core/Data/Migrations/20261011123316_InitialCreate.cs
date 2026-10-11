using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LLMRouter.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "_meta",
                columns: table => new
                {
                    Key = table.Column<string>(type: "TEXT", nullable: false),
                    Value = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK__meta", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "a2aTasks",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Agent = table.Column<string>(type: "TEXT", nullable: true),
                    Payload = table.Column<string>(type: "TEXT", nullable: false),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    Result = table.Column<string>(type: "TEXT", nullable: true),
                    Error = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<string>(type: "TEXT", nullable: false),
                    StartedAt = table.Column<string>(type: "TEXT", nullable: true),
                    FinishedAt = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_a2aTasks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "apiKeys",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Key = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: true),
                    MachineId = table.Column<string>(type: "TEXT", nullable: true),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<string>(type: "TEXT", nullable: false),
                    AccessRestricted = table.Column<bool>(type: "INTEGER", nullable: false),
                    AccessAllow = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_apiKeys", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "auditEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Actor = table.Column<string>(type: "TEXT", nullable: true),
                    Action = table.Column<string>(type: "TEXT", nullable: false),
                    Target = table.Column<string>(type: "TEXT", nullable: true),
                    Meta = table.Column<string>(type: "TEXT", nullable: true),
                    Ip = table.Column<string>(type: "TEXT", nullable: true),
                    At = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_auditEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "cacheEntries",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Hash = table.Column<string>(type: "TEXT", nullable: false),
                    Provider = table.Column<string>(type: "TEXT", nullable: false),
                    Model = table.Column<string>(type: "TEXT", nullable: false),
                    Request = table.Column<string>(type: "TEXT", nullable: false),
                    Response = table.Column<string>(type: "TEXT", nullable: false),
                    Reasoning = table.Column<int>(type: "INTEGER", nullable: false),
                    TokensSaved = table.Column<int>(type: "INTEGER", nullable: false),
                    Hits = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<string>(type: "TEXT", nullable: false),
                    ExpiresAt = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cacheEntries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "capabilityOverrides",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Provider = table.Column<string>(type: "TEXT", nullable: false),
                    Model = table.Column<string>(type: "TEXT", nullable: false),
                    Capabilities = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_capabilityOverrides", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "chatSessions",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    KeyId = table.Column<string>(type: "TEXT", nullable: false),
                    Model = table.Column<string>(type: "TEXT", nullable: false),
                    StartedAt = table.Column<string>(type: "TEXT", nullable: false),
                    LastSeenAt = table.Column<string>(type: "TEXT", nullable: false),
                    MessageCount = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_chatSessions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "cliTokens",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Token = table.Column<string>(type: "TEXT", nullable: false),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    DeviceName = table.Column<string>(type: "TEXT", nullable: true),
                    ApiKey = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<string>(type: "TEXT", nullable: false),
                    ApprovedAt = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cliTokens", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "combos",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: true),
                    StickyLimit = table.Column<int>(type: "INTEGER", nullable: false),
                    Models = table.Column<string>(type: "TEXT", nullable: false),
                    AllowedProviders = table.Column<string>(type: "TEXT", nullable: true),
                    AllowedFamilies = table.Column<string>(type: "TEXT", nullable: true),
                    Tiers = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_combos", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "compressionComboAssignments",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    CompressionComboId = table.Column<string>(type: "TEXT", nullable: false),
                    RoutingComboId = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_compressionComboAssignments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "compressionCombos",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: true),
                    Pipeline = table.Column<string>(type: "TEXT", nullable: false),
                    LanguagePacks = table.Column<string>(type: "TEXT", nullable: false),
                    OutputMode = table.Column<bool>(type: "INTEGER", nullable: false),
                    OutputModeIntensity = table.Column<string>(type: "TEXT", nullable: true),
                    IsDefault = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_compressionCombos", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "compressionRuns",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Timestamp = table.Column<string>(type: "TEXT", nullable: false),
                    PrincipalId = table.Column<string>(type: "TEXT", nullable: true),
                    Model = table.Column<string>(type: "TEXT", nullable: true),
                    EngineId = table.Column<string>(type: "TEXT", nullable: false),
                    BeforeChars = table.Column<long>(type: "INTEGER", nullable: false),
                    AfterChars = table.Column<long>(type: "INTEGER", nullable: false),
                    ComboId = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_compressionRuns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "conductorTasks",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Goal = table.Column<string>(type: "TEXT", nullable: false),
                    Model = table.Column<string>(type: "TEXT", nullable: true),
                    Steps = table.Column<string>(type: "TEXT", nullable: false),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    Result = table.Column<string>(type: "TEXT", nullable: true),
                    Error = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<string>(type: "TEXT", nullable: false),
                    FinishedAt = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_conductorTasks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "credentialExpirations",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    ConnectionId = table.Column<string>(type: "TEXT", nullable: false),
                    ExpiresAt = table.Column<string>(type: "TEXT", nullable: false),
                    WarnDays = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_credentialExpirations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "discoveryResults",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    ProviderId = table.Column<string>(type: "TEXT", nullable: false),
                    Method = table.Column<string>(type: "TEXT", nullable: false),
                    Endpoint = table.Column<string>(type: "TEXT", nullable: true),
                    AuthType = table.Column<string>(type: "TEXT", nullable: false),
                    Models = table.Column<string>(type: "TEXT", nullable: false),
                    RateLimit = table.Column<string>(type: "TEXT", nullable: true),
                    Feasibility = table.Column<int>(type: "INTEGER", nullable: false),
                    RiskLevel = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    Notes = table.Column<string>(type: "TEXT", nullable: true),
                    DiscoveredAt = table.Column<string>(type: "TEXT", nullable: false),
                    VerifiedAt = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_discoveryResults", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "evalCases",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    SuiteId = table.Column<string>(type: "TEXT", nullable: false),
                    Input = table.Column<string>(type: "TEXT", nullable: false),
                    ExpectType = table.Column<string>(type: "TEXT", nullable: false),
                    ExpectValue = table.Column<string>(type: "TEXT", nullable: false),
                    Weight = table.Column<double>(type: "REAL", nullable: false),
                    Ord = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_evalCases", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "evalRuns",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    SuiteId = table.Column<string>(type: "TEXT", nullable: false),
                    Target = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    Score = table.Column<double>(type: "REAL", nullable: false),
                    Results = table.Column<string>(type: "TEXT", nullable: false),
                    StartedAt = table.Column<string>(type: "TEXT", nullable: false),
                    DurationMs = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_evalRuns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "evalSuites",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_evalSuites", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "fallbackChains",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Steps = table.Column<string>(type: "TEXT", nullable: false),
                    Active = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_fallbackChains", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "files",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Filename = table.Column<string>(type: "TEXT", nullable: false),
                    Bytes = table.Column<long>(type: "INTEGER", nullable: false),
                    Purpose = table.Column<string>(type: "TEXT", nullable: false),
                    Mime = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_files", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "gamiItems",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    ItemKey = table.Column<string>(type: "TEXT", nullable: false),
                    Actor = table.Column<string>(type: "TEXT", nullable: true),
                    Points = table.Column<long>(type: "INTEGER", nullable: false),
                    Data = table.Column<string>(type: "TEXT", nullable: true),
                    At = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_gamiItems", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "issueAgentRuns",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Source = table.Column<string>(type: "TEXT", nullable: false),
                    Issue = table.Column<string>(type: "TEXT", nullable: false),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    PrUrl = table.Column<string>(type: "TEXT", nullable: true),
                    Detail = table.Column<string>(type: "TEXT", nullable: true),
                    At = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_issueAgentRuns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "jobRuns",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    JobId = table.Column<string>(type: "TEXT", nullable: false),
                    StartedAt = table.Column<string>(type: "TEXT", nullable: false),
                    DurationMs = table.Column<long>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    Output = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_jobRuns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "jobStates",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    LastRun = table.Column<string>(type: "TEXT", nullable: true),
                    NextRun = table.Column<string>(type: "TEXT", nullable: true),
                    LastStatus = table.Column<string>(type: "TEXT", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", nullable: true),
                    LastDurationMs = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_jobStates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "keyGroups",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    KeysJson = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_keyGroups", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "kv",
                columns: table => new
                {
                    Scope = table.Column<string>(type: "TEXT", nullable: false),
                    Key = table.Column<string>(type: "TEXT", nullable: false),
                    Value = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_kv", x => new { x.Scope, x.Key });
                });

            migrationBuilder.CreateTable(
                name: "logExportDestinations",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Type = table.Column<string>(type: "TEXT", nullable: false),
                    Config = table.Column<string>(type: "TEXT", nullable: false),
                    Filters = table.Column<string>(type: "TEXT", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    LastRunAt = table.Column<string>(type: "TEXT", nullable: true),
                    LastRunStatus = table.Column<string>(type: "TEXT", nullable: true),
                    LastRunDetail = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_logExportDestinations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "mcpToolCalls",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Server = table.Column<string>(type: "TEXT", nullable: true),
                    Tool = table.Column<string>(type: "TEXT", nullable: false),
                    ArgsHash = table.Column<string>(type: "TEXT", nullable: true),
                    DurationMs = table.Column<long>(type: "INTEGER", nullable: false),
                    Ok = table.Column<bool>(type: "INTEGER", nullable: false),
                    At = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mcpToolCalls", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "modelCooldowns",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Provider = table.Column<string>(type: "TEXT", nullable: false),
                    Model = table.Column<string>(type: "TEXT", nullable: false),
                    Until = table.Column<string>(type: "TEXT", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_modelCooldowns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "playgroundPresets",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Model = table.Column<string>(type: "TEXT", nullable: false),
                    ParamsJson = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_playgroundPresets", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "policies",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Priority = table.Column<int>(type: "INTEGER", nullable: false),
                    Rule = table.Column<string>(type: "TEXT", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_policies", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "poolSessions",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    PoolId = table.Column<string>(type: "TEXT", nullable: false),
                    ConnectionId = table.Column<string>(type: "TEXT", nullable: true),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    Health = table.Column<string>(type: "TEXT", nullable: false),
                    BusyUntil = table.Column<string>(type: "TEXT", nullable: true),
                    CooldownUntil = table.Column<string>(type: "TEXT", nullable: true),
                    LastUsedAt = table.Column<string>(type: "TEXT", nullable: true),
                    TotalRequests = table.Column<long>(type: "INTEGER", nullable: false),
                    SuccessfulRequests = table.Column<long>(type: "INTEGER", nullable: false),
                    ConsecutiveFails = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_poolSessions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "providerConnections",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Provider = table.Column<string>(type: "TEXT", nullable: false),
                    AuthType = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: true),
                    Email = table.Column<string>(type: "TEXT", nullable: true),
                    Priority = table.Column<int>(type: "INTEGER", nullable: true),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    Data = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_providerConnections", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "providerNodes",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Type = table.Column<string>(type: "TEXT", nullable: true),
                    Name = table.Column<string>(type: "TEXT", nullable: true),
                    Data = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_providerNodes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "proxyPools",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    TestStatus = table.Column<string>(type: "TEXT", nullable: true),
                    Data = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_proxyPools", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "quotaPlans",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    LimitsJson = table.Column<string>(type: "TEXT", nullable: false),
                    Price = table.Column<double>(type: "REAL", nullable: true),
                    CreatedAt = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_quotaPlans", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "quotaSchedules",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Target = table.Column<string>(type: "TEXT", nullable: false),
                    Window = table.Column<string>(type: "TEXT", nullable: false),
                    LastRunAt = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_quotaSchedules", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "quotaWindows",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Provider = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    WindowMinutes = table.Column<int>(type: "INTEGER", nullable: false),
                    MaxTokens = table.Column<long>(type: "INTEGER", nullable: false),
                    MaxRequests = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_quotaWindows", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "radarItems",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    ItemKey = table.Column<string>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: true),
                    Data = table.Column<string>(type: "TEXT", nullable: true),
                    At = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_radarItems", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "rateLimits",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Scope = table.Column<string>(type: "TEXT", nullable: false),
                    ScopeValue = table.Column<string>(type: "TEXT", nullable: false),
                    Rpm = table.Column<int>(type: "INTEGER", nullable: false),
                    Tpm = table.Column<int>(type: "INTEGER", nullable: false),
                    Burst = table.Column<int>(type: "INTEGER", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_rateLimits", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "relayTokens",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Token = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: true),
                    AllowedModels = table.Column<string>(type: "TEXT", nullable: false),
                    QuotaRequests = table.Column<int>(type: "INTEGER", nullable: false),
                    QuotaTokens = table.Column<long>(type: "INTEGER", nullable: false),
                    ExpiresAt = table.Column<string>(type: "TEXT", nullable: true),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_relayTokens", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "requestDetails",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Timestamp = table.Column<string>(type: "TEXT", nullable: false),
                    Provider = table.Column<string>(type: "TEXT", nullable: true),
                    Model = table.Column<string>(type: "TEXT", nullable: true),
                    ConnectionId = table.Column<string>(type: "TEXT", nullable: true),
                    Status = table.Column<string>(type: "TEXT", nullable: true),
                    Data = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_requestDetails", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "sessionPools",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Provider = table.Column<string>(type: "TEXT", nullable: false),
                    Strategy = table.Column<string>(type: "TEXT", nullable: false),
                    MinSize = table.Column<int>(type: "INTEGER", nullable: false),
                    MaxSize = table.Column<int>(type: "INTEGER", nullable: false),
                    LeaseSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sessionPools", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "settings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    Data = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_settings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "skillExecutions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Skill = table.Column<string>(type: "TEXT", nullable: false),
                    Command = table.Column<string>(type: "TEXT", nullable: false),
                    ExitCode = table.Column<int>(type: "INTEGER", nullable: false),
                    Stdout = table.Column<string>(type: "TEXT", nullable: true),
                    Stderr = table.Column<string>(type: "TEXT", nullable: true),
                    DurationMs = table.Column<long>(type: "INTEGER", nullable: false),
                    At = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_skillExecutions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "syncedModels",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Provider = table.Column<string>(type: "TEXT", nullable: false),
                    Model = table.Column<string>(type: "TEXT", nullable: false),
                    Available = table.Column<bool>(type: "INTEGER", nullable: false),
                    LastSyncAt = table.Column<string>(type: "TEXT", nullable: false),
                    MaxOutputTokens = table.Column<int>(type: "INTEGER", nullable: true),
                    Vision = table.Column<bool>(type: "INTEGER", nullable: true),
                    Tools = table.Column<bool>(type: "INTEGER", nullable: true),
                    Reasoning = table.Column<bool>(type: "INTEGER", nullable: true),
                    ReasoningEfforts = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_syncedModels", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "tags",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    TargetType = table.Column<string>(type: "TEXT", nullable: false),
                    TargetId = table.Column<string>(type: "TEXT", nullable: false),
                    Value = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tags", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "usageDaily",
                columns: table => new
                {
                    DateKey = table.Column<string>(type: "TEXT", nullable: false),
                    Data = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_usageDaily", x => x.DateKey);
                });

            migrationBuilder.CreateTable(
                name: "usageHistory",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Timestamp = table.Column<string>(type: "TEXT", nullable: false),
                    Provider = table.Column<string>(type: "TEXT", nullable: true),
                    Model = table.Column<string>(type: "TEXT", nullable: true),
                    ConnectionId = table.Column<string>(type: "TEXT", nullable: true),
                    ApiKey = table.Column<string>(type: "TEXT", nullable: true),
                    Endpoint = table.Column<string>(type: "TEXT", nullable: true),
                    PromptTokens = table.Column<long>(type: "INTEGER", nullable: false),
                    CompletionTokens = table.Column<long>(type: "INTEGER", nullable: false),
                    Cost = table.Column<double>(type: "REAL", nullable: false),
                    LatencyMs = table.Column<long>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: true),
                    Tokens = table.Column<string>(type: "TEXT", nullable: true),
                    Meta = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_usageHistory", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "vscodeTokens",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Token = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: true),
                    DefaultCombo = table.Column<string>(type: "TEXT", nullable: true),
                    AllowedCombos = table.Column<string>(type: "TEXT", nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_vscodeTokens", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "webhookDeliveries",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    WebhookId = table.Column<string>(type: "TEXT", nullable: false),
                    Data = table.Column<string>(type: "TEXT", nullable: false),
                    At = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_webhookDeliveries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "webhooks",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Data = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_webhooks", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_a2aTasks_State",
                table: "a2aTasks",
                column: "State");

            migrationBuilder.CreateIndex(
                name: "IX_apiKeys_Key",
                table: "apiKeys",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_chatSessions_KeyId_Model",
                table: "chatSessions",
                columns: new[] { "KeyId", "Model" });

            migrationBuilder.CreateIndex(
                name: "IX_cliTokens_Token",
                table: "cliTokens",
                column: "Token",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_combos_Name",
                table: "combos",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_conductorTasks_State",
                table: "conductorTasks",
                column: "State");

            migrationBuilder.CreateIndex(
                name: "IX_evalCases_SuiteId",
                table: "evalCases",
                column: "SuiteId");

            migrationBuilder.CreateIndex(
                name: "IX_evalRuns_SuiteId",
                table: "evalRuns",
                column: "SuiteId");

            migrationBuilder.CreateIndex(
                name: "IX_poolSessions_PoolId",
                table: "poolSessions",
                column: "PoolId");

            migrationBuilder.CreateIndex(
                name: "IX_providerConnections_Provider_IsActive",
                table: "providerConnections",
                columns: new[] { "Provider", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_providerNodes_Type",
                table: "providerNodes",
                column: "Type");

            migrationBuilder.CreateIndex(
                name: "IX_quotaWindows_Provider",
                table: "quotaWindows",
                column: "Provider");

            migrationBuilder.CreateIndex(
                name: "IX_relayTokens_Token",
                table: "relayTokens",
                column: "Token",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_requestDetails_Model",
                table: "requestDetails",
                column: "Model");

            migrationBuilder.CreateIndex(
                name: "IX_requestDetails_Provider",
                table: "requestDetails",
                column: "Provider");

            migrationBuilder.CreateIndex(
                name: "IX_requestDetails_Timestamp",
                table: "requestDetails",
                column: "Timestamp");

            migrationBuilder.CreateIndex(
                name: "IX_usageHistory_ApiKey",
                table: "usageHistory",
                column: "ApiKey");

            migrationBuilder.CreateIndex(
                name: "IX_usageHistory_ConnectionId",
                table: "usageHistory",
                column: "ConnectionId");

            migrationBuilder.CreateIndex(
                name: "IX_usageHistory_Model",
                table: "usageHistory",
                column: "Model");

            migrationBuilder.CreateIndex(
                name: "IX_usageHistory_Provider",
                table: "usageHistory",
                column: "Provider");

            migrationBuilder.CreateIndex(
                name: "IX_usageHistory_Timestamp",
                table: "usageHistory",
                column: "Timestamp");

            migrationBuilder.CreateIndex(
                name: "IX_vscodeTokens_Token",
                table: "vscodeTokens",
                column: "Token",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "_meta");

            migrationBuilder.DropTable(
                name: "a2aTasks");

            migrationBuilder.DropTable(
                name: "apiKeys");

            migrationBuilder.DropTable(
                name: "auditEvents");

            migrationBuilder.DropTable(
                name: "cacheEntries");

            migrationBuilder.DropTable(
                name: "capabilityOverrides");

            migrationBuilder.DropTable(
                name: "chatSessions");

            migrationBuilder.DropTable(
                name: "cliTokens");

            migrationBuilder.DropTable(
                name: "combos");

            migrationBuilder.DropTable(
                name: "compressionComboAssignments");

            migrationBuilder.DropTable(
                name: "compressionCombos");

            migrationBuilder.DropTable(
                name: "compressionRuns");

            migrationBuilder.DropTable(
                name: "conductorTasks");

            migrationBuilder.DropTable(
                name: "credentialExpirations");

            migrationBuilder.DropTable(
                name: "discoveryResults");

            migrationBuilder.DropTable(
                name: "evalCases");

            migrationBuilder.DropTable(
                name: "evalRuns");

            migrationBuilder.DropTable(
                name: "evalSuites");

            migrationBuilder.DropTable(
                name: "fallbackChains");

            migrationBuilder.DropTable(
                name: "files");

            migrationBuilder.DropTable(
                name: "gamiItems");

            migrationBuilder.DropTable(
                name: "issueAgentRuns");

            migrationBuilder.DropTable(
                name: "jobRuns");

            migrationBuilder.DropTable(
                name: "jobStates");

            migrationBuilder.DropTable(
                name: "keyGroups");

            migrationBuilder.DropTable(
                name: "kv");

            migrationBuilder.DropTable(
                name: "logExportDestinations");

            migrationBuilder.DropTable(
                name: "mcpToolCalls");

            migrationBuilder.DropTable(
                name: "modelCooldowns");

            migrationBuilder.DropTable(
                name: "playgroundPresets");

            migrationBuilder.DropTable(
                name: "policies");

            migrationBuilder.DropTable(
                name: "poolSessions");

            migrationBuilder.DropTable(
                name: "providerConnections");

            migrationBuilder.DropTable(
                name: "providerNodes");

            migrationBuilder.DropTable(
                name: "proxyPools");

            migrationBuilder.DropTable(
                name: "quotaPlans");

            migrationBuilder.DropTable(
                name: "quotaSchedules");

            migrationBuilder.DropTable(
                name: "quotaWindows");

            migrationBuilder.DropTable(
                name: "radarItems");

            migrationBuilder.DropTable(
                name: "rateLimits");

            migrationBuilder.DropTable(
                name: "relayTokens");

            migrationBuilder.DropTable(
                name: "requestDetails");

            migrationBuilder.DropTable(
                name: "sessionPools");

            migrationBuilder.DropTable(
                name: "settings");

            migrationBuilder.DropTable(
                name: "skillExecutions");

            migrationBuilder.DropTable(
                name: "syncedModels");

            migrationBuilder.DropTable(
                name: "tags");

            migrationBuilder.DropTable(
                name: "usageDaily");

            migrationBuilder.DropTable(
                name: "usageHistory");

            migrationBuilder.DropTable(
                name: "vscodeTokens");

            migrationBuilder.DropTable(
                name: "webhookDeliveries");

            migrationBuilder.DropTable(
                name: "webhooks");
        }
    }
}
