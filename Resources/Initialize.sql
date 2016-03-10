CREATE TABLE [__BuildMaster_DbSchemaChanges2]
(
	[Script_Id] INT NOT NULL,
	[Script_Guid] UNIQUEIDENTIFIER NOT NULL,
	[Script_Name] NVARCHAR(200) NOT NULL,
	[Executed_Date] DATETIME NOT NULL,
	[Success_Indicator] CHAR(1) NOT NULL,

	CONSTRAINT [__BuildMaster_DbSchemaChanges2PK]
		PRIMARY KEY NONCLUSTERED ([Script_Guid])
)
GO

CREATE CLUSTERED INDEX [__BuildMaster_DbSchemaChanges2IX]
	ON [__BuildMaster_DbSchemaChanges2] ([Script_Id])
GO
