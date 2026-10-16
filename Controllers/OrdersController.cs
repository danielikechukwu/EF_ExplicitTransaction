using EF_ExplicitTransaction.Data;
using EF_TransactionImplementation.DTOs;
using Microsoft.AspNetCore.Mvc;

// For more information on enabling Web API for empty projects, visit https://go.microsoft.com/fwlink/?LinkID=397860

namespace EF_ExplicitTransaction.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class OrdersController : ControllerBase
    {
        private readonly EF_ExplicitTransactionDbContext _context;

        private readonly IServiceScopeFactory _scopeFactory;

        public OrdersController(EF_ExplicitTransactionDbContext context, IServiceScopeFactory scopeFactory)
        {
            _context = context;
            _scopeFactory = scopeFactory;
        }


        // Place order using explicit transaction
        [HttpPost("Place")]
        public async Task<IActionResult> PlaceOrderAsync([FromBody] OrderCreateDTO orderDto)
        {

            if(!ModelState.IsValid)
            {
                var errorMessages = ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage).ToList();

                return BadRequest(new { Success = false, Message = "Validation failed.", Details = string.Join(" | ", errorMessages) });
            }

            // STEP 2: Begin explicit transaction so all steps behave as ONE atomic unit
            return Ok();
        }
    }
}
