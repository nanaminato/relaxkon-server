using Microsoft.EntityFrameworkCore;
using RelaxKon.Auth.Security.ChildKeys;
using RelaxKon.Auth.Models.Suppliers;

namespace RelaxKon.Auth.Data.Contexts;

public partial class KeyContext : DbContext
{
    public KeyContext(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    private IConfiguration _configuration;
    public KeyContext(DbContextOptions<KeyContext> options, IConfiguration configuration)
        : base(options)
    {
        _configuration = configuration;
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        if (!optionsBuilder.IsConfigured) DatabaseProvider.FromConfiguration(_configuration).Configure(optionsBuilder);
    }
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        if (Database.IsMySql())
            modelBuilder.UseCollation("utf8mb4_0900_ai_ci").HasCharSet("utf8mb4");

        OnModelCreatingPartial(modelBuilder);
        modelBuilder.Entity<SupplierKey>().ToTable("SupplierKeys");
        modelBuilder.Entity<ModelKeyBind>().ToTable("ModelKeyBinds");
        modelBuilder.Entity<ModelKeyBind>()
            .HasIndex(binding => new { binding.ModelId, binding.Enable, binding.Priority })
            .HasDatabaseName("IX_ModelKeyBinds_Routing");
        modelBuilder.Entity<Model>().ToTable("Models");
        modelBuilder.Entity<AvailableModel>().ToTable("AvailableModels");
        modelBuilder.Entity<ChildApiKey>(entity =>
        {
            entity.ToTable("ChildApiKeys");
            entity.Property(key => key.Name).HasMaxLength(100);
            entity.Property(key => key.Prefix).HasMaxLength(11);
            entity.Property(key => key.SecretHash).HasMaxLength(64);
            entity.HasIndex(key => key.SecretHash).IsUnique();
            entity.HasIndex(key => new { key.AccountId, key.Id });
            entity.HasOne(key => key.SupplierKey).WithMany().HasForeignKey(key => key.SupplierKeyId)
                .OnDelete(DeleteBehavior.Cascade);
        });

    }

    partial void OnModelCreatingPartial(ModelBuilder modelBuilder);

    public DbSet<SupplierKey>? SupplierKeys
    {
        get;
        set;
    }
    public DbSet<ModelKeyBind>? ModelKeyBinds
    {
        get;
        set;
    }
    public DbSet<Model>? Models
    {
        get;
        set;
    }

    public DbSet<AvailableModel>? AvailableModels
    {
        get;
        set;
    }
}