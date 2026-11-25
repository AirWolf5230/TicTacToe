using Microsoft.EntityFrameworkCore;

namespace TicTacToe.Data;

public sealed class PlayerEntity
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public ICollection<GameEntity> GamesAsX { get; set; } = new List<GameEntity>();
    public ICollection<GameEntity> GamesAsO { get; set; } = new List<GameEntity>();
}

public sealed class GameEntity
{
    public Guid Id { get; set; }
    public string? RoomId { get; set; }
    public Guid PlayerXId { get; set; }
    public Guid PlayerOId { get; set; }
    public Guid? WinnerId { get; set; }
    public string Result { get; set; } = "";
    public string ConnectionMode { get; set; } = "";
    public DateTime StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public string FinalBoard { get; set; } = "";
    public PlayerEntity PlayerX { get; set; } = null!;
    public PlayerEntity PlayerO { get; set; } = null!;
    public ICollection<MoveEntity> Moves { get; set; } = new List<MoveEntity>();
}

public sealed class MoveEntity
{
    public long Id { get; set; }
    public Guid GameId { get; set; }
    public Guid PlayerId { get; set; }
    public int CellIndex { get; set; }
    public int MoveNumber { get; set; }
    public DateTime CreatedAt { get; set; }
    public GameEntity Game { get; set; } = null!;
}

public sealed class AccountEntity
{
    public Guid Id { get; set; }
    public Guid PlayerId { get; set; }
    public string Login { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string PasswordSalt { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public PlayerEntity Player { get; set; } = null!;
}

public sealed class AppDbContext(string databasePath) : DbContext
{
    static AppDbContext() => SQLitePCL.Batteries_V2.Init();

    public DbSet<PlayerEntity> Players => Set<PlayerEntity>();
    public DbSet<GameEntity> Games => Set<GameEntity>();
    public DbSet<MoveEntity> Moves => Set<MoveEntity>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(databasePath));
        if (directory is not null)
            Directory.CreateDirectory(directory);

        optionsBuilder.UseSqlite($"Data Source={databasePath}");
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
        => ConfigureModel(modelBuilder, includeAccounts: false);

    internal static void ConfigureModel(ModelBuilder modelBuilder, bool includeAccounts)
    {
        modelBuilder.Entity<PlayerEntity>(entity =>
        {
            entity.ToTable("Players");
            entity.HasKey(player => player.Id);
            entity.Property(player => player.Name).HasMaxLength(24).IsRequired();
            entity.Property(player => player.CreatedAt).IsRequired();
        });
        modelBuilder.Entity<GameEntity>(entity =>
        {
            entity.ToTable("Games");
            entity.HasKey(game => game.Id);
            entity.HasIndex(game => game.RoomId).IsUnique();
            entity.HasIndex(game => game.PlayerXId);
            entity.HasIndex(game => game.PlayerOId);
            entity.HasIndex(game => game.WinnerId);
            entity.Property(game => game.Result).HasMaxLength(16).IsRequired();
            entity.Property(game => game.ConnectionMode).HasMaxLength(16).IsRequired();
            entity.Property(game => game.StartedAt).IsRequired();
            entity.Property(game => game.FinishedAt);
            entity.Property(game => game.FinalBoard).IsRequired();
            entity.HasOne(game => game.PlayerX).WithMany(player => player.GamesAsX)
                .HasForeignKey(game => game.PlayerXId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(game => game.PlayerO).WithMany(player => player.GamesAsO)
                .HasForeignKey(game => game.PlayerOId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<PlayerEntity>().WithMany()
                .HasForeignKey(game => game.WinnerId).OnDelete(DeleteBehavior.SetNull);
        });
        modelBuilder.Entity<MoveEntity>(entity =>
        {
            entity.HasKey(move => move.Id);
            entity.Property(move => move.Id).ValueGeneratedOnAdd();
            entity.HasIndex(move => new { move.GameId, move.MoveNumber }).IsUnique();
            entity.HasIndex(move => move.PlayerId);
            entity.Property(move => move.CellIndex).IsRequired();
            entity.Property(move => move.CreatedAt).IsRequired();
            entity.ToTable("Moves", table => table.HasCheckConstraint("CK_Moves_CellIndex", "CellIndex >= 0 AND CellIndex <= 8"));
            entity.HasOne(move => move.Game).WithMany(game => game.Moves)
                .HasForeignKey(move => move.GameId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<PlayerEntity>().WithMany()
                .HasForeignKey(move => move.PlayerId).OnDelete(DeleteBehavior.Restrict);
        });
        if (includeAccounts)
        {
            modelBuilder.Entity<AccountEntity>(entity =>
            {
                entity.ToTable("Accounts");
                entity.HasKey(account => account.Id);
                entity.HasIndex(account => account.Login).IsUnique();
                entity.HasIndex(account => account.PlayerId).IsUnique();
                entity.Property(account => account.Login).HasMaxLength(32).IsRequired();
                entity.Property(account => account.PasswordHash).IsRequired();
                entity.Property(account => account.PasswordSalt).IsRequired();
                entity.Property(account => account.CreatedAt).IsRequired();
                entity.HasOne(account => account.Player).WithMany()
                    .HasForeignKey(account => account.PlayerId).OnDelete(DeleteBehavior.Cascade);
            });
        }
    }
}
