using Microsoft.EntityFrameworkCore;
using SapReplitAPI.Models.Auth;
using SapReplitAPI.Models.CachedProducts;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace SapReplitAPI.Services
{
    public class UserCacheService
    {
        private readonly CacheDbContext _db;

        public UserCacheService(CacheDbContext db)
        {
            _db = db;
        }

        public async Task<List<User>> GetAllUsersAsync()
        {
            return await _db.Users.OrderBy(u => u.Id).ToListAsync();
        }

        public async Task<User?> GetUserByIdAsync(int id)
        {
            return await _db.Users.FindAsync(id);
        }

        public async Task<User> AddUserAsync(User user)
        {
            _db.Users.Add(user);
            await _db.SaveChangesAsync();
            return user;
        }

        public async Task<bool> UpdateUserAsync(User user)
        {
            var exists = await _db.Users.AnyAsync(u => u.Id == user.Id);
            if (!exists) return false;

            _db.Users.Update(user);
            await _db.SaveChangesAsync();
            return true;
        }

        public async Task<bool> DeleteUserAsync(int id)
        {
            var user = await _db.Users.FindAsync(id);
            if (user == null) return false;

            _db.Users.Remove(user);
            await _db.SaveChangesAsync();
            return true;
        }
    }
}