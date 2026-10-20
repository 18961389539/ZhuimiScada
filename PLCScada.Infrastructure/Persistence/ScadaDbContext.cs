using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using PLCScada.Core;

namespace PLCScada.Infrastructure.Persistence;

public sealed class ScadaDbContext(DbContextOptions<ScadaDbContext> options) : DbContext(options)
{
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<Tag> Tags => Set<Tag>();
    public DbSet<ManualOutputPoint> ManualOutputPoints => Set<ManualOutputPoint>();
    public DbSet<ManualOutputBinding> ManualOutputBindings => Set<ManualOutputBinding>();
    public DbSet<HmiPage> HmiPages => Set<HmiPage>();
    public DbSet<HmiControl> HmiControls => Set<HmiControl>();
    public DbSet<Recipe> Recipes => Set<Recipe>();
    public DbSet<RecipeParameter> RecipeParameters => Set<RecipeParameter>();
    public DbSet<RecipeVersion> RecipeVersions => Set<RecipeVersion>();
    public DbSet<RecipeApplyRecord> RecipeApplyRecords => Set<RecipeApplyRecord>();
    public DbSet<AlarmRecord> AlarmRecords => Set<AlarmRecord>();
    public DbSet<OperationLog> OperationLogs => Set<OperationLog>();
    public DbSet<ScadaSetting> ScadaSettings => Set<ScadaSetting>();
    public DbSet<AlarmDefinition> AlarmDefinitions => Set<AlarmDefinition>();
    public DbSet<AlarmSuppression> AlarmSuppressions => Set<AlarmSuppression>();
    public DbSet<SnRecord> SnRecords => Set<SnRecord>();
    public DbSet<SnapshotRule> SnapshotRules => Set<SnapshotRule>();
    public DbSet<SnapshotRulePoint> SnapshotRulePoints => Set<SnapshotRulePoint>();
    public DbSet<SnapshotRecord> SnapshotRecords => Set<SnapshotRecord>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Device>().Property(x => x.Name).UseCollation("NOCASE");
        b.Entity<Device>().HasIndex(x => x.Name).IsUnique();
        b.Entity<Tag>().Property(x => x.Name).UseCollation("NOCASE");
        b.Entity<Tag>().HasIndex(x => x.Name).IsUnique();
        b.Entity<Tag>().HasOne(x => x.Device).WithMany().HasForeignKey(x => x.DeviceId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<ManualOutputPoint>().Property(x => x.Name).UseCollation("NOCASE");
        b.Entity<ManualOutputPoint>().HasIndex(x => new { x.DeviceId, x.Name }).IsUnique();
        b.Entity<ManualOutputPoint>().HasIndex(x => new { x.DeviceId, x.SortOrder });
        b.Entity<ManualOutputPoint>().HasOne(x => x.Device).WithMany().HasForeignKey(x => x.DeviceId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<ManualOutputBinding>().HasKey(x => new { x.DeviceId, x.OutputPointId });
        b.Entity<ManualOutputBinding>().HasIndex(x => new { x.DeviceId, x.TagId }).IsUnique();
        b.Entity<ManualOutputBinding>().HasOne(x => x.Device).WithMany().HasForeignKey(x => x.DeviceId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<ManualOutputBinding>().HasOne(x => x.OutputPoint).WithMany().HasForeignKey(x => x.OutputPointId)
            .OnDelete(DeleteBehavior.Cascade);
        b.Entity<ManualOutputBinding>().HasOne(x => x.Tag).WithMany().HasForeignKey(x => x.TagId).OnDelete(DeleteBehavior.SetNull);
        b.Entity<ManualOutputBinding>().HasIndex(x => x.MonitorTagId);
        b.Entity<ManualOutputBinding>().HasIndex(x => new { x.DeviceId, x.MonitorTagId }).IsUnique();
        b.Entity<ManualOutputBinding>().HasOne(x => x.MonitorTag).WithMany()
            .HasForeignKey(x => x.MonitorTagId).OnDelete(DeleteBehavior.SetNull);
        b.Entity<HmiControl>().HasOne(x => x.Tag).WithMany().HasForeignKey(x => x.TagId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<HmiControl>().HasOne(x => x.HmiPage).WithMany().HasForeignKey(x => x.HmiPageId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<Recipe>().Property(x => x.Name).UseCollation("NOCASE");
        b.Entity<Recipe>().HasIndex(x => x.Name).IsUnique();
        b.Entity<Recipe>().HasIndex(x => x.DeviceId);
        b.Entity<Recipe>().Property(x => x.Revision).IsConcurrencyToken();
        b.Entity<Recipe>().Property(x => x.CreatedAt).HasConversion(x => x.UtcTicks, x => new DateTimeOffset(x, TimeSpan.Zero));
        b.Entity<Recipe>().Property(x => x.UpdatedAt).HasConversion(x => x.UtcTicks, x => new DateTimeOffset(x, TimeSpan.Zero));
        b.Entity<Recipe>().Property(x => x.LastAppliedAt).HasConversion(
            x => x.HasValue ? x.Value.UtcTicks : (long?)null,
            x => x.HasValue ? new DateTimeOffset(x.Value, TimeSpan.Zero) : null);
        b.Entity<RecipeParameter>().HasKey(x => new { x.RecipeId, x.TagId });
        b.Entity<RecipeParameter>().HasOne(x => x.Recipe).WithMany(x => x.Parameters)
            .HasForeignKey(x => x.RecipeId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<RecipeParameter>().HasOne(x => x.Tag).WithMany()
            .HasForeignKey(x => x.TagId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<RecipeParameter>().ToTable("RecipeParameters", table =>
            table.HasCheckConstraint(
                "CK_RecipeParameters_OneValue",
                "(EngineeringValue IS NOT NULL AND BooleanValue IS NULL) OR " +
                "(EngineeringValue IS NULL AND BooleanValue IS NOT NULL)"));
        b.Entity<RecipeVersion>().HasKey(x => new { x.RecipeId, x.Revision });
        b.Entity<RecipeVersion>().HasOne(x => x.Recipe).WithMany()
            .HasForeignKey(x => x.RecipeId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<RecipeVersion>().Property(x => x.CreatedAt)
            .HasConversion(x => x.UtcTicks, x => new DateTimeOffset(x, TimeSpan.Zero));
        b.Entity<RecipeVersion>().Property(x => x.Name).HasMaxLength(100);
        b.Entity<RecipeVersion>().Property(x => x.Description).HasMaxLength(500);
        b.Entity<RecipeVersion>().Property(x => x.ChangeNote).HasMaxLength(200);
        b.Entity<RecipeVersion>().Property(x => x.ParametersJson).IsRequired();
        b.Entity<RecipeVersion>().ToTable("RecipeVersions", table =>
            table.HasCheckConstraint(
                "CK_RecipeVersions_PositiveRevision",
                "Revision > 0 AND SnapshotSchemaVersion > 0"));
        b.Entity<RecipeApplyRecord>().Property(x => x.RecipeName).HasMaxLength(100);
        b.Entity<RecipeApplyRecord>().Property(x => x.Summary).HasMaxLength(2000);
        b.Entity<RecipeApplyRecord>().Property(x => x.OccurredAt)
            .HasConversion(x => x.UtcTicks, x => new DateTimeOffset(x, TimeSpan.Zero));
        b.Entity<RecipeApplyRecord>().HasIndex(x => new { x.RecipeId, x.Id });
        b.Entity<AlarmRecord>().HasKey(x => x.AlarmId);
        b.Entity<AlarmRecord>().HasIndex(x => new { x.Source, x.Status });
        // SQLite can order these integer timestamps directly.
        b.Entity<OperationLog>().Property(x => x.Timestamp).HasConversion(x => x.UtcTicks, x => new DateTimeOffset(x, TimeSpan.Zero));
        b.Entity<AlarmRecord>().Property(x => x.OccurredAt).HasConversion(x => x.UtcTicks, x => new DateTimeOffset(x, TimeSpan.Zero));
        b.Entity<AlarmRecord>().Property(x => x.ClearedAt).HasConversion(
            x => x.HasValue ? x.Value.UtcTicks : (long?)null,
            x => x.HasValue ? new DateTimeOffset(x.Value, TimeSpan.Zero) : null);
        b.Entity<AlarmRecord>().Property(x => x.ShelvedUntil).HasConversion(
            x => x.HasValue ? x.Value.UtcTicks : (long?)null,
            x => x.HasValue ? new DateTimeOffset(x.Value, TimeSpan.Zero) : null);
        b.Entity<OperationLog>().HasIndex(x => x.Timestamp);
        b.Entity<ScadaSetting>().HasKey(x => x.Key);
        b.Entity<AlarmDefinition>().Property(x => x.Name).UseCollation("NOCASE");
        b.Entity<AlarmDefinition>().HasIndex(x => new { x.TagId, x.Name }).IsUnique();
        b.Entity<AlarmDefinition>().HasOne(x => x.Tag).WithMany()
            .HasForeignKey(x => x.TagId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<AlarmDefinition>().Property(x => x.CreatedAt)
            .HasConversion(x => x.UtcTicks, x => new DateTimeOffset(x, TimeSpan.Zero));
        b.Entity<AlarmDefinition>().Property(x => x.UpdatedAt)
            .HasConversion(x => x.UtcTicks, x => new DateTimeOffset(x, TimeSpan.Zero));
        b.Entity<AlarmSuppression>().HasOne(x => x.Device).WithMany()
            .HasForeignKey(x => x.DeviceId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<AlarmSuppression>().Property(x => x.Until)
            .HasConversion(x => x.UtcTicks, x => new DateTimeOffset(x, TimeSpan.Zero));
        b.Entity<AlarmSuppression>().Property(x => x.CreatedAt)
            .HasConversion(x => x.UtcTicks, x => new DateTimeOffset(x, TimeSpan.Zero));
        b.Entity<SnRecord>().HasIndex(x => x.Sn);
        b.Entity<SnRecord>().HasIndex(x => x.CapturedAt);
        b.Entity<SnRecord>().Property(x => x.CapturedAt)
            .HasConversion(x => x.UtcTicks, x => new DateTimeOffset(x, TimeSpan.Zero));
        b.Entity<SnapshotRule>().Property(x => x.Name).UseCollation("NOCASE");
        b.Entity<SnapshotRule>().HasIndex(x => x.Name).IsUnique();
        b.Entity<SnapshotRule>().HasOne(x => x.TriggerTag).WithMany()
            .HasForeignKey(x => x.TriggerTagId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<SnapshotRule>().Property(x => x.CreatedAt)
            .HasConversion(x => x.UtcTicks, x => new DateTimeOffset(x, TimeSpan.Zero));
        b.Entity<SnapshotRule>().Property(x => x.UpdatedAt)
            .HasConversion(x => x.UtcTicks, x => new DateTimeOffset(x, TimeSpan.Zero));
        b.Entity<SnapshotRulePoint>().HasKey(x => new { x.RuleId, x.TagId });
        b.Entity<SnapshotRulePoint>().HasOne(x => x.Rule).WithMany(x => x.Points)
            .HasForeignKey(x => x.RuleId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<SnapshotRulePoint>().HasOne(x => x.Tag).WithMany()
            .HasForeignKey(x => x.TagId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<SnapshotRecord>().HasIndex(x => x.OccurredAt);
        b.Entity<SnapshotRecord>().HasIndex(x => x.Sn);
        b.Entity<SnapshotRecord>().HasIndex(x => x.SnRecordId);
        b.Entity<SnapshotRecord>().Property(x => x.OccurredAt)
            .HasConversion(x => x.UtcTicks, x => new DateTimeOffset(x, TimeSpan.Zero));
    }
}

public sealed class ScadaDbContextFactory : IDesignTimeDbContextFactory<ScadaDbContext>
{
    public ScadaDbContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<ScadaDbContext>()
        .UseSqlite("Data Source=plcscada.design.db").Options);
}