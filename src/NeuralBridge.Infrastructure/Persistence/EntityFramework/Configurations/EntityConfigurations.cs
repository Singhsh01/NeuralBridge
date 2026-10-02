using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NeuralBridge.Domain.Sessions;
using NeuralBridge.Domain.Snippets;
using NeuralBridge.Infrastructure.Identity;

namespace NeuralBridge.Infrastructure.Persistence.EntityFramework.Configurations;

internal sealed class SharedSessionConfiguration : IEntityTypeConfiguration<SharedSession>
{
    public void Configure(EntityTypeBuilder<SharedSession> builder)
    {
        builder.ToTable("Sessions");
        builder.HasKey(s => s.Id);

        // Keys are generated in the domain. ValueGeneratedNever makes EF treat new aggregates
        // and participants discovered through navigations as inserts, not updates.
        builder.Property(s => s.Id).ValueGeneratedNever();
        builder.Property(s => s.PublicId).HasMaxLength(32).IsRequired();
        builder.HasIndex(s => s.PublicId).IsUnique();
        builder.Property(s => s.JoinCode).HasMaxLength(SessionCode.Length).IsFixedLength().IsRequired();
        builder.HasIndex(s => s.JoinCode).IsUnique();
        builder.Property(s => s.PinHash).HasMaxLength(256);
        builder.Property(s => s.OwnerUserId).HasMaxLength(450);
        builder.HasIndex(s => s.OwnerUserId);
        builder.Property(s => s.Status).HasConversion<int>();
        builder.Property(s => s.EndReason).HasConversion<int?>();
        builder.HasIndex(s => new { s.Status, s.ExpiresAt });
        builder.HasIndex(s => new { s.Status, s.EndedAt });

        builder.Ignore(s => s.Code);
        builder.Ignore(s => s.HasPin);
        builder.Ignore(s => s.ActiveParticipantCount);
        builder.Ignore(s => s.TotalParticipantCount);

        builder.HasMany(s => s.Participants)
            .WithOne()
            .HasForeignKey(p => p.SessionId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(s => s.Participants)
            .HasField("_participants")
            .UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

internal sealed class SessionParticipantConfiguration : IEntityTypeConfiguration<SessionParticipant>
{
    public void Configure(EntityTypeBuilder<SessionParticipant> builder)
    {
        builder.ToTable("SessionParticipants");
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedNever();
        builder.Property(p => p.DisplayName).HasMaxLength(SessionParticipant.MaxDisplayNameLength).IsRequired();
        builder.Property(p => p.TokenHash).HasMaxLength(64).IsFixedLength().IsRequired();
        builder.Property(p => p.UserId).HasMaxLength(450);
        builder.Ignore(p => p.IsActive);
    }
}

internal sealed class SessionEventConfiguration : IEntityTypeConfiguration<SessionEvent>
{
    public void Configure(EntityTypeBuilder<SessionEvent> builder)
    {
        builder.ToTable("SessionEvents");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedNever();
        builder.Property(e => e.Type).HasConversion<int>();
        builder.HasIndex(e => e.SessionId);
        builder.HasOne<SharedSession>()
            .WithMany()
            .HasForeignKey(e => e.SessionId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class SavedSnippetConfiguration : IEntityTypeConfiguration<SavedSnippet>
{
    public void Configure(EntityTypeBuilder<SavedSnippet> builder)
    {
        builder.ToTable("SavedSnippets");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).ValueGeneratedNever();
        builder.Property(s => s.UserId).HasMaxLength(450).IsRequired();
        builder.Property(s => s.Title).HasMaxLength(SavedSnippet.MaxTitleLength).IsRequired();
        builder.Property(s => s.Content).IsRequired();
        builder.HasIndex(s => new { s.UserId, s.CreatedAt });
        builder.HasOne<ApplicationUser>()
            .WithMany()
            .HasForeignKey(s => s.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
