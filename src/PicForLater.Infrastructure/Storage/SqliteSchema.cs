namespace PicForLater.Infrastructure.Storage;

internal static class SqliteSchema
{
    internal static IReadOnlyList<SqliteMigration> Migrations { get; } =
    [
        new SqliteMigration(
            1,
            "initial-library-and-job-schema",
            """
            CREATE TABLE SchemaMigrations (
                Version INTEGER NOT NULL PRIMARY KEY,
                Name TEXT NOT NULL,
                SqlChecksum TEXT NOT NULL,
                AppliedAtUtc TEXT NOT NULL
            );

            CREATE TABLE ImageAssets (
                Id TEXT NOT NULL PRIMARY KEY,
                ContentHash TEXT NOT NULL UNIQUE CHECK (length(ContentHash) = 64),
                OriginalRelativePath TEXT NOT NULL UNIQUE,
                ThumbnailRelativePath TEXT NULL,
                MediaType TEXT NOT NULL,
                ByteLength INTEGER NOT NULL CHECK (ByteLength >= 0),
                PixelWidth INTEGER NOT NULL CHECK (PixelWidth > 0),
                PixelHeight INTEGER NOT NULL CHECK (PixelHeight > 0),
                CreatedAtUtc TEXT NOT NULL
            );

            CREATE TABLE ImageItems (
                Id TEXT NOT NULL PRIMARY KEY,
                AssetId TEXT NOT NULL,
                OriginalFileName TEXT NOT NULL,
                SourceKind INTEGER NOT NULL CHECK (SourceKind IN (1, 2)),
                Title TEXT NOT NULL,
                Summary TEXT NOT NULL,
                TitleSource INTEGER NOT NULL CHECK (TitleSource IN (1, 2, 3)),
                SummarySource INTEGER NOT NULL CHECK (SummarySource IN (1, 2, 3)),
                AnalysisState INTEGER NOT NULL CHECK (AnalysisState IN (1, 2, 3, 4)),
                Revision INTEGER NOT NULL CHECK (Revision >= 0),
                CreatedAtUtc TEXT NOT NULL,
                UpdatedAtUtc TEXT NOT NULL,
                DeletedAtUtc TEXT NULL,
                FOREIGN KEY (AssetId) REFERENCES ImageAssets(Id) ON DELETE RESTRICT
            );

            CREATE INDEX IX_ImageItems_Active_CreatedAtUtc
                ON ImageItems(DeletedAtUtc, CreatedAtUtc DESC);
            CREATE INDEX IX_ImageItems_AssetId
                ON ImageItems(AssetId);

            CREATE TABLE ImportJobs (
                Id TEXT NOT NULL PRIMARY KEY,
                StagingRelativePath TEXT NULL,
                FinalRelativePath TEXT NULL,
                OriginalFileName TEXT NOT NULL,
                SourceKind INTEGER NOT NULL CHECK (SourceKind IN (1, 2)),
                State INTEGER NOT NULL CHECK (State IN (1, 2, 3, 4, 5, 6)),
                ContentHash TEXT NULL CHECK (ContentHash IS NULL OR length(ContentHash) = 64),
                ImageItemId TEXT NULL,
                AttemptCount INTEGER NOT NULL CHECK (AttemptCount >= 0),
                LeaseExpiresAtUtc TEXT NULL,
                LastErrorCode TEXT NULL,
                CreatedAtUtc TEXT NOT NULL,
                UpdatedAtUtc TEXT NOT NULL,
                CompletedAtUtc TEXT NULL,
                FOREIGN KEY (ImageItemId) REFERENCES ImageItems(Id) ON DELETE SET NULL
            );

            CREATE INDEX IX_ImportJobs_State_Lease
                ON ImportJobs(State, LeaseExpiresAtUtc, CreatedAtUtc);

            CREATE TABLE AnalysisJobs (
                Id TEXT NOT NULL PRIMARY KEY,
                ImageItemId TEXT NOT NULL,
                Kind INTEGER NOT NULL CHECK (Kind IN (1, 2)),
                InputRevision INTEGER NOT NULL CHECK (InputRevision >= 0),
                State INTEGER NOT NULL CHECK (State IN (1, 2, 3, 4, 5, 6)),
                AttemptCount INTEGER NOT NULL CHECK (AttemptCount >= 0),
                NotBeforeUtc TEXT NOT NULL,
                LeaseExpiresAtUtc TEXT NULL,
                LastErrorCode TEXT NULL,
                CreatedAtUtc TEXT NOT NULL,
                UpdatedAtUtc TEXT NOT NULL,
                CompletedAtUtc TEXT NULL,
                FOREIGN KEY (ImageItemId) REFERENCES ImageItems(Id) ON DELETE CASCADE,
                UNIQUE (ImageItemId, Kind, InputRevision)
            );

            CREATE INDEX IX_AnalysisJobs_State_NotBefore_Lease
                ON AnalysisJobs(State, NotBeforeUtc, LeaseExpiresAtUtc);
            """),
        new SqliteMigration(
            2,
            "library-search-categories-and-recycle-bin",
            """
            CREATE TABLE Categories (
                Id TEXT NOT NULL PRIMARY KEY,
                Name TEXT NOT NULL COLLATE NOCASE UNIQUE,
                CreatedAtUtc TEXT NOT NULL,
                UpdatedAtUtc TEXT NOT NULL
            );

            CREATE TABLE ImageCategories (
                ImageItemId TEXT NOT NULL,
                CategoryId TEXT NOT NULL,
                Source INTEGER NOT NULL CHECK (Source IN (1, 2)),
                CreatedAtUtc TEXT NOT NULL,
                PRIMARY KEY (ImageItemId, CategoryId),
                FOREIGN KEY (ImageItemId) REFERENCES ImageItems(Id) ON DELETE CASCADE,
                FOREIGN KEY (CategoryId) REFERENCES Categories(Id) ON DELETE CASCADE
            );
            CREATE INDEX IX_ImageCategories_CategoryId_ImageItemId
                ON ImageCategories(CategoryId, ImageItemId);

            CREATE TABLE CategoryExclusions (
                ImageItemId TEXT NOT NULL,
                CategoryId TEXT NOT NULL,
                CreatedAtUtc TEXT NOT NULL,
                PRIMARY KEY (ImageItemId, CategoryId),
                FOREIGN KEY (ImageItemId) REFERENCES ImageItems(Id) ON DELETE CASCADE,
                FOREIGN KEY (CategoryId) REFERENCES Categories(Id) ON DELETE CASCADE
            );

            CREATE TABLE AnalysisResults (
                Id TEXT NOT NULL PRIMARY KEY,
                ImageItemId TEXT NOT NULL,
                OcrText TEXT NOT NULL,
                VisualFacts TEXT NOT NULL,
                ModelId TEXT NULL,
                ModelVersion TEXT NULL,
                PromptSchemaVersion TEXT NOT NULL,
                Warnings TEXT NOT NULL,
                GeneratedAtUtc TEXT NOT NULL,
                FOREIGN KEY (ImageItemId) REFERENCES ImageItems(Id) ON DELETE CASCADE
            );
            CREATE INDEX IX_AnalysisResults_ImageItemId_GeneratedAtUtc
                ON AnalysisResults(ImageItemId, GeneratedAtUtc DESC);

            CREATE TABLE Reminders (
                Id TEXT NOT NULL PRIMARY KEY,
                ImageItemId TEXT NOT NULL,
                DueAtUtc TEXT NOT NULL,
                TimeZoneId TEXT NOT NULL,
                ConfirmedLocation TEXT NULL,
                SchedulerId TEXT NOT NULL UNIQUE,
                State INTEGER NOT NULL CHECK (State IN (1, 2, 3, 4, 5)),
                CreatedAtUtc TEXT NOT NULL,
                UpdatedAtUtc TEXT NOT NULL,
                FOREIGN KEY (ImageItemId) REFERENCES ImageItems(Id) ON DELETE CASCADE
            );
            CREATE INDEX IX_Reminders_ImageItemId_State
                ON Reminders(ImageItemId, State);

            CREATE TABLE DeletionJobs (
                Id TEXT NOT NULL PRIMARY KEY,
                ImageItemId TEXT NOT NULL,
                AssetId TEXT NOT NULL,
                OriginalRelativePath TEXT NOT NULL,
                ThumbnailRelativePath TEXT NULL,
                State INTEGER NOT NULL CHECK (State IN (1, 2, 3)),
                AttemptCount INTEGER NOT NULL CHECK (AttemptCount >= 0),
                LastErrorCode TEXT NULL,
                CreatedAtUtc TEXT NOT NULL,
                UpdatedAtUtc TEXT NOT NULL,
                CompletedAtUtc TEXT NULL
            );
            CREATE INDEX IX_DeletionJobs_State_UpdatedAtUtc
                ON DeletionJobs(State, UpdatedAtUtc);
            """),
        new SqliteMigration(
            3,
            "recoverable-analysis-stage-provenance",
            """
            ALTER TABLE AnalysisJobs
                ADD COLUMN CurrentStage INTEGER NOT NULL DEFAULT 0
                CHECK (CurrentStage IN (0, 1, 2, 3, 4));
            ALTER TABLE AnalysisJobs
                ADD COLUMN LeaseOwner TEXT NULL;

            CREATE TABLE AnalysisStageResults (
                Id TEXT NOT NULL PRIMARY KEY,
                AnalysisJobId TEXT NULL,
                ImageItemId TEXT NOT NULL,
                Stage INTEGER NOT NULL CHECK (Stage IN (1, 2, 3, 4)),
                InputRevision INTEGER NOT NULL CHECK (InputRevision >= 0),
                ProviderId TEXT NOT NULL,
                ModelId TEXT NULL,
                ModelVersion TEXT NULL,
                ModelFileHashesJson TEXT NOT NULL,
                LanguageTagsJson TEXT NOT NULL,
                SchemaVersion TEXT NOT NULL,
                PayloadJson TEXT NOT NULL,
                FactText TEXT NOT NULL,
                WarningsJson TEXT NOT NULL,
                GeneratedAtUtc TEXT NOT NULL,
                FOREIGN KEY (AnalysisJobId) REFERENCES AnalysisJobs(Id) ON DELETE CASCADE,
                FOREIGN KEY (ImageItemId) REFERENCES ImageItems(Id) ON DELETE CASCADE
            );
            CREATE UNIQUE INDEX UX_AnalysisStageResults_Job_Stage
                ON AnalysisStageResults(AnalysisJobId, Stage)
                WHERE AnalysisJobId IS NOT NULL;
            CREATE INDEX IX_AnalysisStageResults_ImageItem_Stage_Generated
                ON AnalysisStageResults(ImageItemId, Stage, GeneratedAtUtc DESC);

            INSERT INTO AnalysisStageResults (
                Id, AnalysisJobId, ImageItemId, Stage, InputRevision,
                ProviderId, ModelId, ModelVersion, ModelFileHashesJson,
                LanguageTagsJson, SchemaVersion, PayloadJson, FactText,
                WarningsJson, GeneratedAtUtc)
            SELECT
                Id || '-ocr', NULL, ImageItemId, 1, 0,
                CASE WHEN ModelId IS NULL THEN 'legacy.v2.unknown' ELSE 'legacy.v2.model' END,
                ModelId, ModelVersion, '{}', '[]', PromptSchemaVersion, '{}', OcrText,
                CASE
                    WHEN Warnings = '' THEN '[]'
                    WHEN json_valid(Warnings) AND json_type(Warnings) = 'array' THEN Warnings
                    ELSE json_array(Warnings)
                END,
                GeneratedAtUtc
            FROM AnalysisResults;

            INSERT INTO AnalysisStageResults (
                Id, AnalysisJobId, ImageItemId, Stage, InputRevision,
                ProviderId, ModelId, ModelVersion, ModelFileHashesJson,
                LanguageTagsJson, SchemaVersion, PayloadJson, FactText,
                WarningsJson, GeneratedAtUtc)
            SELECT
                Id || '-vision', NULL, ImageItemId, 3, 0,
                CASE WHEN ModelId IS NULL THEN 'legacy.v2.unknown' ELSE 'legacy.v2.model' END,
                ModelId, ModelVersion, '{}', '[]', PromptSchemaVersion, '{}', VisualFacts,
                CASE
                    WHEN Warnings = '' THEN '[]'
                    WHEN json_valid(Warnings) AND json_type(Warnings) = 'array' THEN Warnings
                    ELSE json_array(Warnings)
                END,
                GeneratedAtUtc
            FROM AnalysisResults
            WHERE VisualFacts <> '';

            DROP TABLE AnalysisResults;
            """),
        new SqliteMigration(
            4,
            "conditional-analysis-and-model-profiles",
            """
            ALTER TABLE AnalysisJobs
                ADD COLUMN AnalysisMode INTEGER NOT NULL DEFAULT 2
                CHECK (AnalysisMode IN (1, 2, 3));
            ALTER TABLE AnalysisJobs
                ADD COLUMN ProfileRevision INTEGER NOT NULL DEFAULT 0
                CHECK (ProfileRevision >= 0);
            ALTER TABLE AnalysisJobs
                ADD COLUMN ModelProfileSnapshotJson TEXT NOT NULL DEFAULT '{}'
                CHECK (json_valid(ModelProfileSnapshotJson));

            CREATE TABLE AnalysisSettings (
                Id INTEGER NOT NULL PRIMARY KEY CHECK (Id = 1),
                AnalysisMode INTEGER NOT NULL CHECK (AnalysisMode IN (1, 2, 3)),
                ProfileRevision INTEGER NOT NULL CHECK (ProfileRevision >= 0),
                UpdatedAtUtc TEXT NOT NULL
            );
            INSERT INTO AnalysisSettings (Id, AnalysisMode, ProfileRevision, UpdatedAtUtc)
            VALUES (1, 2, 1, '1970-01-01T00:00:00.0000000+00:00');

            CREATE TABLE ModelPackages (
                PackageKey TEXT NOT NULL PRIMARY KEY,
                PackageId TEXT NOT NULL,
                Version TEXT NOT NULL,
                Backend TEXT NOT NULL,
                Architecture TEXT NOT NULL,
                Quantization TEXT NOT NULL,
                ManifestJson TEXT NOT NULL CHECK (json_valid(ManifestJson)),
                InstalledRelativePath TEXT NOT NULL UNIQUE,
                BenchmarkStatus TEXT NOT NULL,
                InstalledAtUtc TEXT NOT NULL,
                SelfTestedAtUtc TEXT NOT NULL
            );

            CREATE TABLE ModelCapabilityProfiles (
                Capability INTEGER NOT NULL PRIMARY KEY CHECK (Capability IN (1, 2, 3, 4)),
                ProviderId TEXT NOT NULL,
                PackageKey TEXT NULL,
                Revision INTEGER NOT NULL CHECK (Revision >= 0),
                UpdatedAtUtc TEXT NOT NULL,
                FOREIGN KEY (PackageKey) REFERENCES ModelPackages(PackageKey) ON DELETE RESTRICT
            );
            INSERT INTO ModelCapabilityProfiles (Capability, ProviderId, PackageKey, Revision, UpdatedAtUtc)
            VALUES
                (1, 'local.fallback-ocr', NULL, 1, '1970-01-01T00:00:00.0000000+00:00'),
                (2, 'local.none', NULL, 1, '1970-01-01T00:00:00.0000000+00:00'),
                (3, 'local.extractive-text', NULL, 1, '1970-01-01T00:00:00.0000000+00:00'),
                (4, 'local.deterministic-entities', NULL, 1, '1970-01-01T00:00:00.0000000+00:00');

            CREATE TABLE EntityCandidates (
                Id TEXT NOT NULL PRIMARY KEY,
                AnalysisJobId TEXT NOT NULL,
                ImageItemId TEXT NOT NULL,
                Kind TEXT NOT NULL,
                RawText TEXT NOT NULL,
                NormalizedValue TEXT NULL,
                Evidence TEXT NOT NULL,
                Source TEXT NOT NULL CHECK (Source IN ('Metadata', 'Ocr', 'Model')),
                GeneratedAtUtc TEXT NOT NULL,
                FOREIGN KEY (AnalysisJobId) REFERENCES AnalysisJobs(Id) ON DELETE CASCADE,
                FOREIGN KEY (ImageItemId) REFERENCES ImageItems(Id) ON DELETE CASCADE
            );
            CREATE UNIQUE INDEX UX_EntityCandidates_Job_Kind_Raw_Evidence
                ON EntityCandidates(AnalysisJobId, Kind, RawText, Evidence);
            CREATE INDEX IX_EntityCandidates_ImageItem_Generated
                ON EntityCandidates(ImageItemId, GeneratedAtUtc DESC);
            """),
        new SqliteMigration(
            5,
            "reminder-candidate-evidence-and-notification-outbox",
            """
            ALTER TABLE EntityCandidates
                ADD COLUMN CandidateStatus INTEGER NOT NULL DEFAULT 1
                CHECK (CandidateStatus IN (1, 2, 3));
            ALTER TABLE EntityCandidates
                ADD COLUMN BoundingBoxJson TEXT NULL
                CHECK (BoundingBoxJson IS NULL OR json_valid(BoundingBoxJson));
            ALTER TABLE EntityCandidates
                ADD COLUMN ReferenceTimeUtc TEXT NULL;
            ALTER TABLE EntityCandidates
                ADD COLUMN TimeZoneId TEXT NULL;
            ALTER TABLE EntityCandidates
                ADD COLUMN AmbiguityReason TEXT NULL;
            ALTER TABLE EntityCandidates
                ADD COLUMN ConfirmedReminderId TEXT NULL;

            ALTER TABLE Reminders
                ADD COLUMN SourceDateCandidateId TEXT NULL;
            ALTER TABLE Reminders
                ADD COLUMN SourceLocationCandidateId TEXT NULL;
            ALTER TABLE Reminders
                ADD COLUMN NotificationState INTEGER NOT NULL DEFAULT 1
                CHECK (NotificationState IN (1, 2, 3, 4, 5));
            ALTER TABLE Reminders
                ADD COLUMN NotificationLastErrorCode TEXT NULL;
            ALTER TABLE Reminders
                ADD COLUMN CompletionReason TEXT NULL;
            ALTER TABLE Reminders
                ADD COLUMN ActivatedAtUtc TEXT NULL;
            ALTER TABLE Reminders
                ADD COLUMN LastReconciledAtUtc TEXT NULL;

            CREATE TABLE ReminderNotificationOutbox (
                Id TEXT NOT NULL PRIMARY KEY,
                ReminderId TEXT NULL,
                SchedulerId TEXT NOT NULL,
                Operation INTEGER NOT NULL CHECK (Operation IN (1, 2)),
                DueAtUtc TEXT NULL,
                Title TEXT NULL,
                Body TEXT NULL,
                Location TEXT NULL,
                State INTEGER NOT NULL CHECK (State IN (1, 2, 3, 4)),
                AttemptCount INTEGER NOT NULL CHECK (AttemptCount >= 0),
                NotBeforeUtc TEXT NOT NULL,
                LastErrorCode TEXT NULL,
                CreatedAtUtc TEXT NOT NULL,
                UpdatedAtUtc TEXT NOT NULL,
                CompletedAtUtc TEXT NULL,
                FOREIGN KEY (ReminderId) REFERENCES Reminders(Id) ON DELETE SET NULL
            );
            CREATE INDEX IX_ReminderNotificationOutbox_State_NotBefore
                ON ReminderNotificationOutbox(State, NotBeforeUtc, CreatedAtUtc);
            CREATE INDEX IX_ReminderNotificationOutbox_SchedulerId
                ON ReminderNotificationOutbox(SchedulerId, State);
            CREATE INDEX IX_EntityCandidates_Pending_Kind_Generated
                ON EntityCandidates(CandidateStatus, Kind, GeneratedAtUtc DESC);
            """),
        new SqliteMigration(
            6,
            "independent-reminder-titles",
            """
            ALTER TABLE Reminders
                ADD COLUMN Title TEXT NULL
                CHECK (Title IS NULL OR length(trim(Title)) BETWEEN 1 AND 300);
            """),
        new SqliteMigration(
            7,
            "explicit-analysis-stage-execution-and-output-kind",
            """
            ALTER TABLE AnalysisStageResults
                ADD COLUMN ExecutionLocation INTEGER NOT NULL DEFAULT 0
                CHECK (ExecutionLocation IN (0, 1));
            ALTER TABLE AnalysisStageResults
                ADD COLUMN OutputKind INTEGER NOT NULL DEFAULT 0
                CHECK (OutputKind IN (0, 1, 2, 3, 4, 5));

            UPDATE AnalysisStageResults
            SET OutputKind = CASE
                WHEN Stage = 1 THEN 1
                WHEN Stage = 2 THEN 2
                WHEN Stage = 3 AND ModelId IS NULL THEN 3
                WHEN Stage IN (3, 4) AND ModelId IS NOT NULL THEN 4
                WHEN Stage = 4 THEN 5
                ELSE 0
            END;
            """),
        new SqliteMigration(
            8,
            "remote-api-profiles-and-execution-selection",
            """
            CREATE TABLE RemoteApiProfiles (
                ProfileId TEXT NOT NULL PRIMARY KEY,
                ProviderId TEXT NOT NULL,
                DisplayName TEXT NOT NULL,
                EndpointId TEXT NOT NULL,
                BaseUri TEXT NOT NULL,
                ModelId TEXT NOT NULL,
                SupportedInputModesJson TEXT NOT NULL
                    CHECK (json_valid(SupportedInputModesJson)),
                PromptVersion TEXT NOT NULL,
                OutputSchemaVersion TEXT NOT NULL,
                MaxTextChars INTEGER NOT NULL CHECK (MaxTextChars > 0),
                MaxImageBytes INTEGER NOT NULL CHECK (MaxImageBytes > 0),
                MaxOutputTokens INTEGER NOT NULL CHECK (MaxOutputTokens > 0),
                TimeoutSeconds INTEGER NOT NULL CHECK (TimeoutSeconds > 0),
                PrivacyUrl TEXT NOT NULL,
                TermsUrl TEXT NOT NULL,
                RetentionTrainingStatement TEXT NOT NULL,
                RetentionTrainingVerifiedAtUtc TEXT NOT NULL,
                CredentialReference TEXT NOT NULL,
                DisclosureVersion TEXT NOT NULL,
                IsEnabled INTEGER NOT NULL CHECK (IsEnabled IN (0, 1)),
                ValidationState INTEGER NOT NULL CHECK (ValidationState IN (0, 1, 2)),
                LastVerifiedAtUtc TEXT NULL,
                ConsentedInputMode INTEGER NULL
                    CHECK (ConsentedInputMode IS NULL OR ConsentedInputMode IN (1, 2)),
                ConsentedDisclosureVersion TEXT NULL,
                ConsentGrantedAtUtc TEXT NULL,
                UpdatedAtUtc TEXT NOT NULL,
                CHECK (
                    (ConsentedInputMode IS NULL
                        AND ConsentedDisclosureVersion IS NULL
                        AND ConsentGrantedAtUtc IS NULL)
                    OR
                    (ConsentedInputMode IS NOT NULL
                        AND ConsentedDisclosureVersion IS NOT NULL
                        AND ConsentGrantedAtUtc IS NOT NULL)
                )
            );
            CREATE INDEX IX_RemoteApiProfiles_Provider_Enabled
                ON RemoteApiProfiles(ProviderId, IsEnabled, DisplayName);

            ALTER TABLE AnalysisSettings
                ADD COLUMN ExecutionBackend INTEGER NOT NULL DEFAULT 0
                CHECK (ExecutionBackend IN (0, 1));
            ALTER TABLE AnalysisSettings
                ADD COLUMN RemoteInputMode INTEGER NULL
                CHECK (RemoteInputMode IS NULL OR RemoteInputMode IN (1, 2));
            ALTER TABLE AnalysisSettings
                ADD COLUMN RemoteApiProfileId TEXT NULL;
            """),
        new SqliteMigration(
            9,
            "remote-input-mode-stage-provenance",
            """
            ALTER TABLE AnalysisStageResults
                ADD COLUMN RemoteInputMode INTEGER NULL
                CHECK (RemoteInputMode IS NULL OR RemoteInputMode IN (1, 2));
            """),
        new SqliteMigration(
            10,
            "explicit-analysis-stage-outcome",
            """
            ALTER TABLE AnalysisStageResults
                ADD COLUMN StageOutcome INTEGER NOT NULL DEFAULT 0
                CHECK (StageOutcome IN (0, 1));
            """),
        new SqliteMigration(
            11,
            "remote-api-protocol-and-endpoint-policy",
            """
            ALTER TABLE RemoteApiProfiles
                ADD COLUMN Protocol INTEGER NOT NULL DEFAULT 0
                CHECK (Protocol IN (0, 1));
            ALTER TABLE RemoteApiProfiles
                ADD COLUMN AuthenticationKind INTEGER NOT NULL DEFAULT 0
                CHECK (AuthenticationKind IN (0, 1, 2));
            ALTER TABLE RemoteApiProfiles
                ADD COLUMN StructuredOutputMode INTEGER NOT NULL DEFAULT 0
                CHECK (StructuredOutputMode IN (0, 1));
            ALTER TABLE RemoteApiProfiles
                ADD COLUMN EndpointTrustMode INTEGER NOT NULL DEFAULT 0
                CHECK (EndpointTrustMode IN (0, 1, 2));
            ALTER TABLE RemoteApiProfiles ADD COLUMN ApiVersion TEXT NULL;
            ALTER TABLE RemoteApiProfiles
                ADD COLUMN DisableProviderFallbacks INTEGER NOT NULL DEFAULT 0
                CHECK (DisableProviderFallbacks IN (0, 1));
            ALTER TABLE RemoteApiProfiles
                ADD COLUMN DisableExternalSearch INTEGER NOT NULL DEFAULT 0
                CHECK (DisableExternalSearch IN (0, 1));
            """),
        new SqliteMigration(
            12,
            "remote-api-output-mode-and-explicit-reasoning",
            """
            ALTER TABLE RemoteApiProfiles
                ADD COLUMN StructuredOutputModeV2 INTEGER NOT NULL DEFAULT 0
                CHECK (StructuredOutputModeV2 IN (0, 1, 2));
            UPDATE RemoteApiProfiles
                SET StructuredOutputModeV2 = StructuredOutputMode;
            ALTER TABLE RemoteApiProfiles
                ADD COLUMN ReasoningMode INTEGER NOT NULL DEFAULT 0
                CHECK (ReasoningMode IN (0, 1, 2, 3, 4));
            ALTER TABLE RemoteApiProfiles
                ADD COLUMN ReasoningWireFormat INTEGER NOT NULL DEFAULT 0
                CHECK (ReasoningWireFormat IN (0, 1, 2, 3, 4));
            """),
        new SqliteMigration(
            13,
            "reminder-reconciliation-query-indexes",
            """
            CREATE INDEX IX_Reminders_State_DueAtUtc_Id
                ON Reminders(State, DueAtUtc, Id);

            UPDATE ReminderNotificationOutbox
            SET Id = lower(
                substr(replace(Id, '-', ''), 1, 8) || '-' ||
                substr(replace(Id, '-', ''), 9, 4) || '-' ||
                substr(replace(Id, '-', ''), 13, 4) || '-' ||
                substr(replace(Id, '-', ''), 17, 4) || '-' ||
                substr(replace(Id, '-', ''), 21, 12))
            WHERE length(replace(Id, '-', '')) = 32
              AND replace(Id, '-', '') NOT GLOB '*[^0-9A-Fa-f]*';
            """),
        new SqliteMigration(
            14,
            "localsend-image-source-kind",
            """
            CREATE TABLE ImageItems_LocalSendMigration (
                Id TEXT NOT NULL PRIMARY KEY,
                AssetId TEXT NOT NULL,
                OriginalFileName TEXT NOT NULL,
                SourceKind INTEGER NOT NULL CHECK (SourceKind IN (1, 2, 3)),
                Title TEXT NOT NULL,
                Summary TEXT NOT NULL,
                TitleSource INTEGER NOT NULL CHECK (TitleSource IN (1, 2, 3)),
                SummarySource INTEGER NOT NULL CHECK (SummarySource IN (1, 2, 3)),
                AnalysisState INTEGER NOT NULL CHECK (AnalysisState IN (1, 2, 3, 4)),
                Revision INTEGER NOT NULL CHECK (Revision >= 0),
                CreatedAtUtc TEXT NOT NULL,
                UpdatedAtUtc TEXT NOT NULL,
                DeletedAtUtc TEXT NULL,
                FOREIGN KEY (AssetId) REFERENCES ImageAssets(Id) ON DELETE RESTRICT
            );

            INSERT INTO ImageItems_LocalSendMigration (
                Id, AssetId, OriginalFileName, SourceKind, Title, Summary,
                TitleSource, SummarySource, AnalysisState, Revision,
                CreatedAtUtc, UpdatedAtUtc, DeletedAtUtc)
            SELECT
                Id, AssetId, OriginalFileName, SourceKind, Title, Summary,
                TitleSource, SummarySource, AnalysisState, Revision,
                CreatedAtUtc, UpdatedAtUtc, DeletedAtUtc
            FROM ImageItems;

            DROP TABLE ImageItems;
            ALTER TABLE ImageItems_LocalSendMigration RENAME TO ImageItems;
            CREATE INDEX IX_ImageItems_Active_CreatedAtUtc
                ON ImageItems(DeletedAtUtc, CreatedAtUtc DESC);
            CREATE INDEX IX_ImageItems_AssetId
                ON ImageItems(AssetId);

            CREATE TABLE ImportJobs_LocalSendMigration (
                Id TEXT NOT NULL PRIMARY KEY,
                StagingRelativePath TEXT NULL,
                FinalRelativePath TEXT NULL,
                OriginalFileName TEXT NOT NULL,
                SourceKind INTEGER NOT NULL CHECK (SourceKind IN (1, 2, 3)),
                State INTEGER NOT NULL CHECK (State IN (1, 2, 3, 4, 5, 6)),
                ContentHash TEXT NULL CHECK (ContentHash IS NULL OR length(ContentHash) = 64),
                ImageItemId TEXT NULL,
                AttemptCount INTEGER NOT NULL CHECK (AttemptCount >= 0),
                LeaseExpiresAtUtc TEXT NULL,
                LastErrorCode TEXT NULL,
                CreatedAtUtc TEXT NOT NULL,
                UpdatedAtUtc TEXT NOT NULL,
                CompletedAtUtc TEXT NULL,
                FOREIGN KEY (ImageItemId) REFERENCES ImageItems(Id) ON DELETE SET NULL
            );

            INSERT INTO ImportJobs_LocalSendMigration (
                Id, StagingRelativePath, FinalRelativePath, OriginalFileName,
                SourceKind, State, ContentHash, ImageItemId, AttemptCount,
                LeaseExpiresAtUtc, LastErrorCode, CreatedAtUtc, UpdatedAtUtc,
                CompletedAtUtc)
            SELECT
                Id, StagingRelativePath, FinalRelativePath, OriginalFileName,
                SourceKind, State, ContentHash, ImageItemId, AttemptCount,
                LeaseExpiresAtUtc, LastErrorCode, CreatedAtUtc, UpdatedAtUtc,
                CompletedAtUtc
            FROM ImportJobs;

            DROP TABLE ImportJobs;
            ALTER TABLE ImportJobs_LocalSendMigration RENAME TO ImportJobs;
            CREATE INDEX IX_ImportJobs_State_Lease
                ON ImportJobs(State, LeaseExpiresAtUtc, CreatedAtUtc);
            """,
            requiresForeignKeysDisabled: true),
    ];
}
