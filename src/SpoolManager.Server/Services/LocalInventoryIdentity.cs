using LinqToDB;
using SpoolManager.Infrastructure.Data;
using SpoolManager.Infrastructure.Repositories;
using SpoolManager.Shared.Models;

namespace SpoolManager.Server.Services;

// Retain the original inventory identity for foreign keys and existing favorites.
// This is a shared installation identity, never a browser credential or login.
public sealed record LocalInventoryIdentity(Guid Id)
{
    public static async Task<LocalInventoryIdentity> InitializeAsync(SpoolManagerDb db, ISiteSettingsRepository settings)
    {
        var saved = await settings.GetAsync("local_inventory_user_id");
        var user = Guid.TryParse(saved, out var id) ? await db.Users.FirstOrDefaultAsync(u => u.Id == id) : null;
        var migrateFavorites = user == null;
        user ??= await db.Users.OrderByDescending(u => u.IsSuperAdmin).ThenByDescending(u => u.IsPlatformAdmin)
            .ThenBy(u => u.CreatedAt).ThenBy(u => u.Id).FirstOrDefaultAsync();
        if (user == null)
        {
            user = new AppUser { Id = Guid.NewGuid(), Username = "Shared inventory", Email = "local@spoolhero.invalid",
                PasswordHash = "disabled", IsPlatformAdmin = true, IsSuperAdmin = true, EmailVerified = true,
                CreatedAt = DateTime.UtcNow };
            await db.InsertAsync(user);
        }
        await using var transaction = await db.BeginTransactionAsync();
        var projects = await db.Projects.ToListAsync();
        if (projects.Count == 0)
        {
            var project = new Project { Id = Guid.NewGuid(), Name = "Shared inventory", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
            await db.InsertAsync(project);
            projects.Add(project);
        }
        foreach (var project in projects)
        {
            var member = await db.ProjectMembers.FirstOrDefaultAsync(m => m.ProjectId == project.Id && m.UserId == user.Id);
            if (member == null)
                await db.InsertAsync(new ProjectMember { Id = Guid.NewGuid(), ProjectId = project.Id, UserId = user.Id, Role = "admin", JoinedAt = DateTime.UtcNow });
            else if (member.Role != "admin")
                await db.ProjectMembers.Where(m => m.Id == member.Id).Set(m => m.Role, "admin").UpdateAsync();
        }
        // Every browser now uses the same favorites. Preserve all existing selections.
        var favorites = migrateFavorites ? await db.UserMaterialFavorites.Where(f => f.UserId != user.Id).ToListAsync() : [];
        foreach (var favorite in favorites)
            if (!await db.UserMaterialFavorites.AnyAsync(f => f.UserId == user.Id && f.MaterialId == favorite.MaterialId))
                await db.InsertAsync(new UserMaterialFavorite { UserId = user.Id, MaterialId = favorite.MaterialId, CreatedAt = favorite.CreatedAt });
        await settings.SetAsync("local_inventory_user_id", user.Id.ToString());
        await transaction.CommitAsync();
        return new(user.Id);
    }
}
