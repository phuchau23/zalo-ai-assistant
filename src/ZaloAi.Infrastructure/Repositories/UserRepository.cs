using Microsoft.EntityFrameworkCore;
using ZaloAi.Core.Entities;
using ZaloAi.Infrastructure.Persistence;

namespace ZaloAi.Infrastructure.Repositories;

/// <summary>
/// users là bảng toàn cục (không thuộc tenant): một người có thể ở nhiều tenant. Không trả dữ liệu tenant nào ở đây.
/// </summary>
public sealed class UserRepository(AppDbContext db)
{
    public Task<User?> FindByEmailAsync(string email, CancellationToken cancellationToken)
    {
        var normalized = User.NormalizeEmail(email);
        return db.Users.Where(u => u.Email == normalized).FirstOrDefaultAsync(cancellationToken);
    }

    public User Add(string email, string passwordHash, string name, bool isSuperAdmin = false)
    {
        var user = new User
        {
            Id = Guid.CreateVersion7(),
            Email = User.NormalizeEmail(email),
            PasswordHash = passwordHash,
            Name = name,
            IsSuperAdmin = isSuperAdmin,
        };
        db.Users.Add(user);
        return user;
    }

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken) => db.SaveChangesAsync(cancellationToken);
}
