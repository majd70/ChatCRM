using ChatCRM.Application.Interfaces;
using ChatCRM.Application.Users.DTOS;
using ChatCRM.Domain.Entities;
using ChatCRM.Infrastructure.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ChatCRM.MVC.Controllers
{
    [Authorize]
    public class RolesController : Controller
    {
        private readonly IRoleManagementService _service;
        private readonly ILogger<RolesController> _logger;

        public RolesController(IRoleManagementService service, ILogger<RolesController> logger)
        {
            _service = service;
            _logger = logger;
        }

        [HttpGet("/dashboard/settings/roles")]
        [RequirePermission(Permissions.RolesManage)]
        public IActionResult Index()
        {
            ViewBag.Groups = Permissions.Groups;
            ViewBag.Labels = Permissions.Labels;
            return View();
        }

        [HttpGet("/api/roles")]
        [RequirePermission(Permissions.RolesManage)]
        public async Task<IActionResult> List(CancellationToken cancellationToken)
            => Ok(await _service.ListAsync(cancellationToken));

        [HttpGet("/api/roles/{id}")]
        [RequirePermission(Permissions.RolesManage)]
        public async Task<IActionResult> Get(string id, CancellationToken cancellationToken)
        {
            var r = await _service.GetAsync(id, cancellationToken);
            return r is null ? NotFound() : Ok(r);
        }

        [HttpPost("/api/roles")]
        [RequirePermission(Permissions.RolesManage)]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Save([FromBody] SaveRoleDto dto, CancellationToken cancellationToken)
        {
            try
            {
                var saved = await _service.SaveAsync(dto, cancellationToken);
                return Ok(saved);
            }
            catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        }

        [HttpDelete("/api/roles/{id}")]
        [RequirePermission(Permissions.RolesManage)]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(string id, CancellationToken cancellationToken)
        {
            try
            {
                await _service.DeleteAsync(id, cancellationToken);
                return Ok(new { deleted = true });
            }
            catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        }
    }
}
