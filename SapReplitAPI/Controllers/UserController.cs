using Microsoft.AspNetCore.Mvc;
using SapReplitAPI.Models.Auth;
using SapReplitAPI.Services;
using System.Threading.Tasks;

namespace SapReplitAPI.Controllers
{
    [ApiController]
    [Route("api/users")]
    public class UserController : ControllerBase
    {
        private readonly UserCacheService _userService;

        public UserController(UserCacheService userService)
        {
            _userService = userService;
        }

        // GET: /api/users
        [HttpGet]
        public async Task<IActionResult> GetAll()
        {
            var users = await _userService.GetAllUsersAsync();
            return Ok(users);
        }

        // GET: /api/users/{id}
        [HttpGet("{id}")]
        public async Task<IActionResult> Get(int id)
        {
            var user = await _userService.GetUserByIdAsync(id);
            return user == null ? NotFound() : Ok(user);
        }

        // POST: /api/users
        [HttpPost]
        public async Task<IActionResult> Create([FromBody] User user)
        {
            var created = await _userService.AddUserAsync(user);
            return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
        }

        // PUT: /api/users/{id}
        [HttpPut("{id}")]
        public async Task<IActionResult> Update(int id, [FromBody] User user)
        {
            if (id != user.Id) return BadRequest("Mismatched ID");
            var updated = await _userService.UpdateUserAsync(user);
            return updated ? Ok(user) : NotFound();
        }

        // DELETE: /api/users/{id}
        [HttpDelete("{id}")]
        public async Task<IActionResult> Delete(int id)
        {
            var deleted = await _userService.DeleteUserAsync(id);
            return deleted ? NoContent() : NotFound();
        }
    }
}