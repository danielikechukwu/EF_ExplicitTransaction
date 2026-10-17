using EF_ExplicitTransaction.Data;
using EF_TransactionImplementation.DTOs;
using EF_TransactionImplementation.Entities;
using EF_TransactionImplementation.Enums;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

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
        public async Task<IActionResult> PlaceOrderAsync([FromBody] OrderCreateDTO dto)
        {

            if (!ModelState.IsValid)
            {
                var errorMessages = ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage).ToList();

                return BadRequest(new { Success = false, Message = "Validation failed.", Details = string.Join(" | ", errorMessages) });
            }

            // STEP 2: Begin explicit transaction so all steps behave as ONE atomic unit
            using var transaction = await _context.Database.BeginTransactionAsync();

            try
            {
                // STEP 3: Load customer and their address details
                var customer = await _context.Customers.Include(c => c.Addresses).FirstOrDefaultAsync(c => c.Id == dto.CustomerId && c.IsActive == true);

                if(customer == null)
                {
                    return NotFound(new { Success = false, Message = "Customer not found or inactive." });
                };

                // STEP 4: Validate shipping & billing addresses
                var shippingAddress = customer.Addresses.FirstOrDefault(a => a.Id == dto.ShippingAddressId && a.IsActive);

                var billingAddress = customer.Addresses
                    .FirstOrDefault(a => a.Id == dto.BillingAddressId && a.IsActive);

                if(shippingAddress == null || billingAddress == null)
                {
                    return BadRequest(new
                    {
                        Success = false,
                        Message = "Invalid shipping or billing address."
                    });
                }

                // STEP 5: Load all requested products in a single DB call
                var productIds = dto.Items.Select(i => i.ProductId).Distinct().ToList();

                var products = await _context.Products.Where(p => productIds.Contains(p.Id) && p.IsActive).ToListAsync();

                if (products.Count != productIds.Count)
                {
                    return BadRequest(new
                    {
                        Success = false,
                        Message = "Invalid Products"
                    });
                }

                // STEP 6: Validate stock & build OrderItems list
                decimal orderTotal = 0;

                List<OrderItem> orderItems = new List<OrderItem>();

                foreach (var item in dto.Items)
                {
                    var product = products.First(p => p.Id == item.ProductId);

                    if(product.StockQuantity < item.Quantity)
                    {
                        return BadRequest(new
                        {
                            Success = false,
                            Message = $"Insufficient stock for product: {product.Name}"
                        });
                    }

                    var lineTotal = product.FinalPrice * item.Quantity;

                    orderTotal += lineTotal;

                    orderItems.Add(new OrderItem
                    {
                        ProductId = product.Id,
                        Quantity = (int)item.Quantity,
                        UnitPrice = product.FinalPrice,
                        TotalPrice = lineTotal
                    });

                    // Reduce inventory (actual DB update happens during SaveChanges)
                    product.StockQuantity -= (int)item.Quantity;

                }

                // STEP 7: Create Order with initial status = Pending
                var order = new Order
                {
                    CustomerId = customer.Id,
                    OrderNumber = Guid.NewGuid().ToString()[..8].ToUpper(),
                    TotalAmount = orderTotal,
                    OrderDate = DateTime.UtcNow,
                    OrderStatus = OrderStatus.Pending,
                    ShippingAddress =
                        $"{shippingAddress.Line1}, {shippingAddress.City}, {shippingAddress.State}, {shippingAddress.Country}, {shippingAddress.ZipCode}",
                    BillingAddress =
                        $"{billingAddress.Line1}, {billingAddress.City}, {billingAddress.State}, {billingAddress.Country}, {billingAddress.ZipCode}"
                };

                await _context.Orders.AddAsync(order);

                // Attach OrderItems to Order
                foreach(var item in orderItems)
                {
                    item.Order = order;
                    await _context.OrderItems.AddAsync(item);
                }

                // STEP 8: Create Payment with initial status = Pending
                var payment = new Payment
                {
                    Order = order,
                    Amount = orderTotal,
                    PaymentStatus = PaymentStatus.Pending,
                    PaymentMethod = dto.Payment.PaymentMethod,
                    TransactionId = string.IsNullOrWhiteSpace(dto.Payment.TransactionId)
                        ? Guid.NewGuid().ToString()
                        : dto.Payment.TransactionId,
                    PaymentDate = DateTime.UtcNow
                };

                await _context.Payments.AddAsync(payment);

                // STEP 9: Insert first OrderHistory entry
                await _context.OrderHistories.AddAsync(new OrderHistory
                {
                    Order = order,
                    OldStatus = OrderStatus.Pending,
                    NewStatus = OrderStatus.Pending,
                    Remarks = "Order created successfully"
                });

                // FIRST SAVE (Order + Items + Payment + History)
                await _context.SaveChangesAsync();

                // STEP 10: Simulated payment success for teaching purpose
                var oldOrderStatus = order.OrderStatus;

                payment.PaymentStatus = PaymentStatus.Paid;
                order.OrderStatus = OrderStatus.PaymentReceived;

                await _context.OrderHistories.AddAsync(new OrderHistory
                {
                    OrderId = order.Id,
                    OldStatus = oldOrderStatus,
                    NewStatus = OrderStatus.PaymentReceived,
                    Remarks = "Payment verified successfully"
                });

                // Simulated Failure
                if(dto.MakeSecondSaveFail)
                {
                    throw new Exception("Simulated failure occurred before second SaveChanges.");
                }

                // SECOND SAVE (status updates)
                await _context.SaveChangesAsync();

                // STEP 11: Commit the entire explicit transaction
                await transaction.CommitAsync();

                return Ok(new OrderResponseDTO
                {
                    OrderId = order.Id,
                    OrderNumber = order.OrderNumber,
                    Message = "Order placed successfully using Explicit Transaction."
                });


            }
            catch (Exception ex)
            {
                // STEP 12: Rollback entire workflow on ANY exception
                await transaction.RollbackAsync();

                // Dispose transaction before using another DbContext
                await transaction.DisposeAsync();

                // STEP 13: Log the exception into FailureLog table
                try
                {
                    using var scope = _scopeFactory.CreateScope();

                    var loggingContext = scope.ServiceProvider.GetRequiredService<EF_ExplicitTransactionDbContext>();

                    var logEntry = new FailureLog
                    {
                        OrderId = null,                 // Order may not exist
                        CustomerId = dto.CustomerId,    // Helps investigation
                        ClassName = "OrdersController",
                        MethodName = "PlaceOrderAsync",
                        ErrorMessage = ex.Message,
                        StackTrace = ex.StackTrace ?? "",
                        RequestPayload = JsonSerializer.Serialize(dto),
                        Severity = "Error",
                        Environment = "Production"
                    };

                    loggingContext.FailureLogs.Add(logEntry);

                    await loggingContext.SaveChangesAsync();

                }
                catch
                {

                }

                return StatusCode(500, new
                {
                    Success = false,
                    Message = "Transaction failed.",
                    Details = ex.Message
                });

            }

        }
    }
}
