using McpProxy.Data;
using Microsoft.EntityFrameworkCore;

namespace McpProxy.Models;

/// <summary>
/// Adds the model-router tables to databases created by pre-model-router releases. Fresh databases
/// are created by EF EnsureCreated; this class only bridges the existing project's no-migrations model.
/// </summary>
public static class ModelSchemaUpgrade
{
    /// <summary>Creates missing model-router tables and indexes without altering existing MCP tables.</summary>
    public static async Task EnsureAsync(ProxyDbContext db, CancellationToken cancellationToken = default)
    {
        var provider = db.Database.ProviderName ?? "";
        if (provider.Contains("InMemory", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (provider.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
        {
            await db.Database.ExecuteSqlRawAsync(SqliteSql, cancellationToken);
            return;
        }

        if (provider.Contains("Npgsql", StringComparison.OrdinalIgnoreCase))
        {
            await db.Database.ExecuteSqlRawAsync(PostgresSql, cancellationToken);
            return;
        }

        if (provider.Contains("SqlServer", StringComparison.OrdinalIgnoreCase))
        {
            await db.Database.ExecuteSqlRawAsync(SqlServerSql, cancellationToken);
            return;
        }

        throw new InvalidOperationException($"Unsupported database provider for model-router schema upgrade: {provider}");
    }

    private const string SqliteSql = """
CREATE TABLE IF NOT EXISTS "ModelProviders" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_ModelProviders" PRIMARY KEY,
    "Name" TEXT NOT NULL,
    "Slug" TEXT NOT NULL,
    "Kind" INTEGER NOT NULL,
    "BaseEndpoint" TEXT NOT NULL,
    "ChatPath" TEXT NULL,
    "Enabled" INTEGER NOT NULL,
    "CredentialReference" TEXT NULL,
    "CredentialHeader" TEXT NOT NULL,
    "CredentialPrefix" TEXT NOT NULL,
    "AwsRegion" TEXT NULL,
    "AwsAccessKeyReference" TEXT NULL,
    "AwsSecretKeyReference" TEXT NULL,
    "AwsSessionTokenReference" TEXT NULL,
    "CreatedAt" TEXT NOT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS "IX_ModelProviders_Slug" ON "ModelProviders" ("Slug");

CREATE TABLE IF NOT EXISTS "ModelRoutes" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_ModelRoutes" PRIMARY KEY,
    "ProviderId" TEXT NOT NULL,
    "PublicName" TEXT NOT NULL,
    "DownstreamModel" TEXT NOT NULL,
    "Enabled" INTEGER NOT NULL,
    "CreatedAt" TEXT NOT NULL,
    CONSTRAINT "FK_ModelRoutes_ModelProviders_ProviderId" FOREIGN KEY ("ProviderId")
        REFERENCES "ModelProviders" ("Id") ON DELETE CASCADE
);
CREATE UNIQUE INDEX IF NOT EXISTS "IX_ModelRoutes_PublicName" ON "ModelRoutes" ("PublicName");
CREATE INDEX IF NOT EXISTS "IX_ModelRoutes_ProviderId" ON "ModelRoutes" ("ProviderId");

CREATE TABLE IF NOT EXISTS "ModelRouteTargets" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_ModelRouteTargets" PRIMARY KEY,
    "ModelRouteId" TEXT NOT NULL,
    "ProviderId" TEXT NOT NULL,
    "DownstreamModel" TEXT NOT NULL,
    "Priority" INTEGER NOT NULL,
    "Weight" INTEGER NOT NULL,
    "Enabled" INTEGER NOT NULL,
    "CreatedAt" TEXT NOT NULL,
    CONSTRAINT "FK_ModelRouteTargets_ModelRoutes_ModelRouteId" FOREIGN KEY ("ModelRouteId")
        REFERENCES "ModelRoutes" ("Id") ON DELETE CASCADE,
    CONSTRAINT "FK_ModelRouteTargets_ModelProviders_ProviderId" FOREIGN KEY ("ProviderId")
        REFERENCES "ModelProviders" ("Id") ON DELETE RESTRICT
);
CREATE UNIQUE INDEX IF NOT EXISTS "IX_ModelRouteTargets_ModelRouteId_ProviderId_DownstreamModel"
    ON "ModelRouteTargets" ("ModelRouteId", "ProviderId", "DownstreamModel");
CREATE INDEX IF NOT EXISTS "IX_ModelRouteTargets_ModelRouteId_Priority_Enabled"
    ON "ModelRouteTargets" ("ModelRouteId", "Priority", "Enabled");
CREATE INDEX IF NOT EXISTS "IX_ModelRouteTargets_ProviderId" ON "ModelRouteTargets" ("ProviderId");

CREATE TABLE IF NOT EXISTS "ModelPermissions" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_ModelPermissions" PRIMARY KEY,
    "RoleId" TEXT NOT NULL,
    "Scope" INTEGER NOT NULL,
    "ProviderId" TEXT NULL,
    "ModelRouteId" TEXT NULL,
    CONSTRAINT "FK_ModelPermissions_Roles_RoleId" FOREIGN KEY ("RoleId")
        REFERENCES "Roles" ("Id") ON DELETE CASCADE,
    CONSTRAINT "FK_ModelPermissions_ModelProviders_ProviderId" FOREIGN KEY ("ProviderId")
        REFERENCES "ModelProviders" ("Id") ON DELETE RESTRICT,
    CONSTRAINT "FK_ModelPermissions_ModelRoutes_ModelRouteId" FOREIGN KEY ("ModelRouteId")
        REFERENCES "ModelRoutes" ("Id") ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS "IX_ModelPermissions_RoleId" ON "ModelPermissions" ("RoleId");
CREATE INDEX IF NOT EXISTS "IX_ModelPermissions_ProviderId" ON "ModelPermissions" ("ProviderId");
CREATE INDEX IF NOT EXISTS "IX_ModelPermissions_ModelRouteId" ON "ModelPermissions" ("ModelRouteId");
CREATE INDEX IF NOT EXISTS "IX_ModelPermissions_RoleId_Scope_ProviderId_ModelRouteId"
    ON "ModelPermissions" ("RoleId", "Scope", "ProviderId", "ModelRouteId");
""";

    private const string PostgresSql = """
CREATE TABLE IF NOT EXISTS "ModelProviders" (
    "Id" uuid NOT NULL PRIMARY KEY,
    "Name" text NOT NULL,
    "Slug" text NOT NULL,
    "Kind" integer NOT NULL,
    "BaseEndpoint" text NOT NULL,
    "ChatPath" text NULL,
    "Enabled" boolean NOT NULL,
    "CredentialReference" text NULL,
    "CredentialHeader" text NOT NULL,
    "CredentialPrefix" text NOT NULL,
    "AwsRegion" text NULL,
    "AwsAccessKeyReference" text NULL,
    "AwsSecretKeyReference" text NULL,
    "AwsSessionTokenReference" text NULL,
    "CreatedAt" timestamp with time zone NOT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS "IX_ModelProviders_Slug" ON "ModelProviders" ("Slug");

CREATE TABLE IF NOT EXISTS "ModelRoutes" (
    "Id" uuid NOT NULL PRIMARY KEY,
    "ProviderId" uuid NOT NULL REFERENCES "ModelProviders" ("Id") ON DELETE CASCADE,
    "PublicName" text NOT NULL,
    "DownstreamModel" text NOT NULL,
    "Enabled" boolean NOT NULL,
    "CreatedAt" timestamp with time zone NOT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS "IX_ModelRoutes_PublicName" ON "ModelRoutes" ("PublicName");
CREATE INDEX IF NOT EXISTS "IX_ModelRoutes_ProviderId" ON "ModelRoutes" ("ProviderId");

CREATE TABLE IF NOT EXISTS "ModelRouteTargets" (
    "Id" uuid NOT NULL PRIMARY KEY,
    "ModelRouteId" uuid NOT NULL REFERENCES "ModelRoutes" ("Id") ON DELETE CASCADE,
    "ProviderId" uuid NOT NULL REFERENCES "ModelProviders" ("Id") ON DELETE RESTRICT,
    "DownstreamModel" text NOT NULL,
    "Priority" integer NOT NULL,
    "Weight" integer NOT NULL,
    "Enabled" boolean NOT NULL,
    "CreatedAt" timestamp with time zone NOT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS "IX_ModelRouteTargets_ModelRouteId_ProviderId_DownstreamModel"
    ON "ModelRouteTargets" ("ModelRouteId", "ProviderId", "DownstreamModel");
CREATE INDEX IF NOT EXISTS "IX_ModelRouteTargets_ModelRouteId_Priority_Enabled"
    ON "ModelRouteTargets" ("ModelRouteId", "Priority", "Enabled");
CREATE INDEX IF NOT EXISTS "IX_ModelRouteTargets_ProviderId" ON "ModelRouteTargets" ("ProviderId");

CREATE TABLE IF NOT EXISTS "ModelPermissions" (
    "Id" uuid NOT NULL PRIMARY KEY,
    "RoleId" uuid NOT NULL REFERENCES "Roles" ("Id") ON DELETE CASCADE,
    "Scope" integer NOT NULL,
    "ProviderId" uuid NULL REFERENCES "ModelProviders" ("Id") ON DELETE RESTRICT,
    "ModelRouteId" uuid NULL REFERENCES "ModelRoutes" ("Id") ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS "IX_ModelPermissions_RoleId" ON "ModelPermissions" ("RoleId");
CREATE INDEX IF NOT EXISTS "IX_ModelPermissions_ProviderId" ON "ModelPermissions" ("ProviderId");
CREATE INDEX IF NOT EXISTS "IX_ModelPermissions_ModelRouteId" ON "ModelPermissions" ("ModelRouteId");
CREATE INDEX IF NOT EXISTS "IX_ModelPermissions_RoleId_Scope_ProviderId_ModelRouteId"
    ON "ModelPermissions" ("RoleId", "Scope", "ProviderId", "ModelRouteId");
""";

    private const string SqlServerSql = """
IF OBJECT_ID(N'[ModelProviders]', N'U') IS NULL
BEGIN
    CREATE TABLE [ModelProviders] (
        [Id] uniqueidentifier NOT NULL CONSTRAINT [PK_ModelProviders] PRIMARY KEY,
        [Name] nvarchar(max) NOT NULL,
        [Slug] nvarchar(450) NOT NULL,
        [Kind] int NOT NULL,
        [BaseEndpoint] nvarchar(max) NOT NULL,
        [ChatPath] nvarchar(max) NULL,
        [Enabled] bit NOT NULL,
        [CredentialReference] nvarchar(max) NULL,
        [CredentialHeader] nvarchar(max) NOT NULL,
        [CredentialPrefix] nvarchar(max) NOT NULL,
        [AwsRegion] nvarchar(max) NULL,
        [AwsAccessKeyReference] nvarchar(max) NULL,
        [AwsSecretKeyReference] nvarchar(max) NULL,
        [AwsSessionTokenReference] nvarchar(max) NULL,
        [CreatedAt] datetimeoffset NOT NULL
    );
    CREATE UNIQUE INDEX [IX_ModelProviders_Slug] ON [ModelProviders] ([Slug]);
END;

IF OBJECT_ID(N'[ModelRoutes]', N'U') IS NULL
BEGIN
    CREATE TABLE [ModelRoutes] (
        [Id] uniqueidentifier NOT NULL CONSTRAINT [PK_ModelRoutes] PRIMARY KEY,
        [ProviderId] uniqueidentifier NOT NULL,
        [PublicName] nvarchar(450) NOT NULL,
        [DownstreamModel] nvarchar(max) NOT NULL,
        [Enabled] bit NOT NULL,
        [CreatedAt] datetimeoffset NOT NULL,
        CONSTRAINT [FK_ModelRoutes_ModelProviders_ProviderId] FOREIGN KEY ([ProviderId])
            REFERENCES [ModelProviders] ([Id]) ON DELETE CASCADE
    );
    CREATE UNIQUE INDEX [IX_ModelRoutes_PublicName] ON [ModelRoutes] ([PublicName]);
    CREATE INDEX [IX_ModelRoutes_ProviderId] ON [ModelRoutes] ([ProviderId]);
END;

IF OBJECT_ID(N'[ModelRouteTargets]', N'U') IS NULL
BEGIN
    CREATE TABLE [ModelRouteTargets] (
        [Id] uniqueidentifier NOT NULL CONSTRAINT [PK_ModelRouteTargets] PRIMARY KEY,
        [ModelRouteId] uniqueidentifier NOT NULL,
        [ProviderId] uniqueidentifier NOT NULL,
        [DownstreamModel] nvarchar(450) NOT NULL,
        [Priority] int NOT NULL,
        [Weight] int NOT NULL,
        [Enabled] bit NOT NULL,
        [CreatedAt] datetimeoffset NOT NULL,
        CONSTRAINT [FK_ModelRouteTargets_ModelRoutes_ModelRouteId] FOREIGN KEY ([ModelRouteId])
            REFERENCES [ModelRoutes] ([Id]) ON DELETE CASCADE,
        CONSTRAINT [FK_ModelRouteTargets_ModelProviders_ProviderId] FOREIGN KEY ([ProviderId])
            REFERENCES [ModelProviders] ([Id])
    );
    CREATE UNIQUE INDEX [IX_ModelRouteTargets_ModelRouteId_ProviderId_DownstreamModel]
        ON [ModelRouteTargets] ([ModelRouteId], [ProviderId], [DownstreamModel]);
    CREATE INDEX [IX_ModelRouteTargets_ModelRouteId_Priority_Enabled]
        ON [ModelRouteTargets] ([ModelRouteId], [Priority], [Enabled]);
    CREATE INDEX [IX_ModelRouteTargets_ProviderId] ON [ModelRouteTargets] ([ProviderId]);
END;

IF OBJECT_ID(N'[ModelPermissions]', N'U') IS NULL
BEGIN
    CREATE TABLE [ModelPermissions] (
        [Id] uniqueidentifier NOT NULL CONSTRAINT [PK_ModelPermissions] PRIMARY KEY,
        [RoleId] uniqueidentifier NOT NULL,
        [Scope] int NOT NULL,
        [ProviderId] uniqueidentifier NULL,
        [ModelRouteId] uniqueidentifier NULL,
        CONSTRAINT [FK_ModelPermissions_Roles_RoleId] FOREIGN KEY ([RoleId])
            REFERENCES [Roles] ([Id]) ON DELETE CASCADE,
        CONSTRAINT [FK_ModelPermissions_ModelProviders_ProviderId] FOREIGN KEY ([ProviderId])
            REFERENCES [ModelProviders] ([Id]),
        CONSTRAINT [FK_ModelPermissions_ModelRoutes_ModelRouteId] FOREIGN KEY ([ModelRouteId])
            REFERENCES [ModelRoutes] ([Id]) ON DELETE CASCADE
    );
    CREATE INDEX [IX_ModelPermissions_RoleId] ON [ModelPermissions] ([RoleId]);
    CREATE INDEX [IX_ModelPermissions_ProviderId] ON [ModelPermissions] ([ProviderId]);
    CREATE INDEX [IX_ModelPermissions_ModelRouteId] ON [ModelPermissions] ([ModelRouteId]);
    CREATE INDEX [IX_ModelPermissions_RoleId_Scope_ProviderId_ModelRouteId]
        ON [ModelPermissions] ([RoleId], [Scope], [ProviderId], [ModelRouteId]);
END;
""";
}
