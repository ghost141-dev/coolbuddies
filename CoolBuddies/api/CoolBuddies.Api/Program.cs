// CoolBuddies API (.NET 8). Setup:
//   dotnet new web -n CoolBuddies.Api ; cd CoolBuddies.Api ; replace Program.cs with this file
//   dotnet add package Dapper ; dotnet add package Microsoft.Data.SqlClient
// Settings (use environment variables or `dotnet user-secrets`, never commit them):
//   ConnectionStrings__Default  PayFast__MerchantId  PayFast__MerchantKey  PayFast__Passphrase
//   PayFast__Sandbox (true while testing)  PublicSiteUrl (your website)  PublicApiUrl (this API, https)
using System.Globalization;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using Dapper;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Data.SqlClient;

// Creates the admin password hash:  dotnet run -- hash-password "your long password"
if (args.Length == 2 && args[0] == "hash-password") { Console.WriteLine(Passwords.Hash(args[1])); return; }

var b = WebApplication.CreateBuilder(args);
var cfg = b.Configuration;
var cs = cfg.GetConnectionString("Default") ?? throw new InvalidOperationException("Missing connection string");
var pf = new PayFast(cfg["PayFast:MerchantId"]!, cfg["PayFast:MerchantKey"]!, cfg["PayFast:Passphrase"] ?? "", cfg.GetValue<bool>("PayFast:Sandbox"));
var site = cfg["PublicSiteUrl"]!.TrimEnd('/');
var api = cfg["PublicApiUrl"]!.TrimEnd('/');
var adminHash = cfg["Admin:PasswordHash"] ?? "";

b.WebHost.ConfigureKestrel(k => { k.AddServerHeader = false; k.Limits.MaxRequestBodySize = 32 * 1024; });
b.Services.AddHttpClient();
b.Services.Configure<ForwardedHeadersOptions>(o => { o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto; o.KnownNetworks.Clear(); o.KnownProxies.Clear(); });
b.Services.AddCors(o => o.AddDefaultPolicy(p => p.WithOrigins(site).WithMethods("GET", "POST").WithHeaders("Content-Type", "Authorization")));
b.Services.AddRateLimiter(o => {
    o.RejectionStatusCode = 429;
    o.AddPolicy("api", c => RateLimitPartition.GetFixedWindowLimiter(c.Connection.RemoteIpAddress?.ToString() ?? "x",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 20, Window = TimeSpan.FromMinutes(1) }));
    o.AddPolicy("login", c => RateLimitPartition.GetFixedWindowLimiter("login-" + (c.Connection.RemoteIpAddress?.ToString() ?? "x"),
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 5, Window = TimeSpan.FromMinutes(1) }));
});

var app = b.Build();
app.UseForwardedHeaders();
if (!app.Environment.IsDevelopment()) {
    app.UseExceptionHandler(e => e.Run(c => { c.Response.StatusCode = 500; return c.Response.WriteAsync("Server error"); }));
    app.UseHsts();
}
app.UseHttpsRedirection();
app.Use(async (c, next) => {
    var h = c.Response.Headers;
    h["X-Content-Type-Options"] = "nosniff"; h["X-Frame-Options"] = "DENY"; h["Referrer-Policy"] = "no-referrer";
    h["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'"; h["Cache-Control"] = "no-store";
    await next();
});
app.UseCors();
app.UseRateLimiter();

app.MapGet("/health", () => "ok");

app.MapGet("/api/products", async () => {
    await using var db = new SqlConnection(cs);
    return Results.Ok(await db.QueryAsync<ProductOut>("SELECT Id, Name, Description, Price FROM Products WHERE Active = 1"));
}).RequireRateLimiting("api");

// Creates a pending order. Prices always come from the database, never from the browser.
app.MapPost("/api/orders", async (OrderIn o) => {
    if (o.Validate() is { } err) return Results.BadRequest(new { error = err });
    await using var db = new SqlConnection(cs);
    await db.OpenAsync();
    await using var tx = await db.BeginTransactionAsync();
    var ids = o.Items.Select(i => i.ProductId).ToArray();
    var prods = (await db.QueryAsync<Prod>("SELECT Id, Name, Price FROM Products WHERE Active = 1 AND Id IN @ids", new { ids }, tx)).ToDictionary(p => p.Id);
    if (prods.Count != ids.Length) return Results.BadRequest(new { error = "Unknown product" });
    var total = o.Items.Sum(i => prods[i.ProductId].Price * i.Qty);
    var rf = Guid.NewGuid();
    var oid = await db.ExecuteScalarAsync<int>(
        @"INSERT INTO Orders (Reference, FirstName, LastName, Email, Phone, Address, Notes, Total)
          OUTPUT INSERTED.Id VALUES (@rf, @FirstName, @LastName, @Email, @Phone, @Address, @Notes, @total)",
        new { rf, o.FirstName, o.LastName, o.Email, o.Phone, o.Address, o.Notes, total }, tx);
    foreach (var i in o.Items)
        await db.ExecuteAsync("INSERT INTO OrderItems (OrderId, ProductId, Qty, UnitPrice) VALUES (@oid, @p, @q, @u)",
            new { oid, p = i.ProductId, q = i.Qty, u = prods[i.ProductId].Price }, tx);
    await tx.CommitAsync();
    var fields = pf.Build(rf.ToString("N"), total, "CoolBuddies order", o, $"{site}/?paid=1", $"{site}/?cancelled=1", $"{api}/api/payments/notify");
    return Results.Ok(new { action = pf.Url + "/eng/process", fields });
}).RequireRateLimiting("api");

// PayFast calls this after payment. An order is only marked Paid after four checks pass.
app.MapPost("/api/payments/notify", async (HttpRequest r, IHttpClientFactory hf) => {
    var form = await r.ReadFormAsync();
    var body = string.Join("&", form.Where(k => k.Key != "signature").Select(k => $"{k.Key}={Enc(k.Value.ToString())}"));
    var signed = pf.Passphrase != "" ? body + "&passphrase=" + Enc(pf.Passphrase) : body;
    if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Md5(signed)), Encoding.ASCII.GetBytes(form["signature"].ToString().ToLowerInvariant())))
        return Results.BadRequest();                                                        // 1. signature matches
    using var http = hf.CreateClient();
    var res = await http.PostAsync(pf.Url + "/eng/query/validate", new StringContent(body, Encoding.UTF8, "application/x-www-form-urlencoded"));
    if ((await res.Content.ReadAsStringAsync()).Trim() != "VALID") return Results.BadRequest(); // 2. PayFast confirms it sent this
    if (!Guid.TryParseExact(form["m_payment_id"].ToString(), "N", out var rf)) return Results.Ok();
    await using var db = new SqlConnection(cs);
    var order = await db.QuerySingleOrDefaultAsync<OrderRow>("SELECT Id, Total FROM Orders WHERE Reference = @rf", new { rf });
    if (order is null || form["payment_status"] != "COMPLETE") return Results.Ok();
    if (!decimal.TryParse(form["amount_gross"].ToString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var paid) || paid != order.Total)
        return Results.Ok();                                                                // 3. amount equals our order total
    await db.ExecuteAsync(                                                                  // 4. safe to repeat: recorded once only
        @"IF NOT EXISTS (SELECT 1 FROM Payments WHERE GatewayPaymentId = @g)
          BEGIN
            INSERT INTO Payments (OrderId, GatewayPaymentId, Amount) VALUES (@id, @g, @a);
            UPDATE Orders SET Status = 'Paid', PaidAt = SYSUTCDATETIME() WHERE Id = @id AND Status = 'Pending';
          END", new { id = order.Id, g = form["pf_payment_id"].ToString(), a = order.Total });
    return Results.Ok();
});

// ---- Admin: password login gives a 2-hour token, which the orders list requires ----
app.MapPost("/api/admin/login", async (LoginIn l, IDataProtectionProvider dp) => {
    await Task.Delay(300);                                   // slows down password guessing
    if (adminHash == "" || !Passwords.Verify(l.Password ?? "", adminHash)) return Results.Unauthorized();
    return Results.Ok(new { token = dp.CreateProtector("coolbuddies.admin").ToTimeLimitedDataProtector().Protect("admin", TimeSpan.FromHours(2)) });
}).RequireRateLimiting("login");

var admin = app.MapGroup("/api/admin").RequireRateLimiting("api").AddEndpointFilter(async (ctx, next) => {
    var auth = ctx.HttpContext.Request.Headers.Authorization.ToString();
    try {
        var dp = ctx.HttpContext.RequestServices.GetRequiredService<IDataProtectionProvider>();
        if (auth.StartsWith("Bearer ") && dp.CreateProtector("coolbuddies.admin").ToTimeLimitedDataProtector().Unprotect(auth[7..]) == "admin")
            return await next(ctx);
    } catch { }
    return Results.Unauthorized();
});

admin.MapGet("/orders", async (string? status) => {
    await using var db = new SqlConnection(cs);
    return Results.Ok(await db.QueryAsync<AdminOrder>(
        @"SELECT TOP 200 o.Id, o.FirstName + ' ' + o.LastName AS Customer, o.Email, o.Phone, o.Address, o.Notes, o.Total, o.Status, o.CreatedAt, o.PaidAt,
                 (SELECT STRING_AGG(CONCAT(p.Name, ' x', i.Qty), ', ') FROM OrderItems i JOIN Products p ON p.Id = i.ProductId WHERE i.OrderId = o.Id) AS Items
          FROM Orders o WHERE (@status IS NULL OR o.Status = @status) ORDER BY o.Id DESC",
        new { status = (status is "Pending" or "Paid" or "Failed") ? status : null }));
});

app.Run();

static string Enc(string s) => Uri.EscapeDataString(s).Replace("%20", "+").Replace("~", "%7E");
static string Md5(string s) => Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();

record Item(int ProductId, int Qty);
record Prod(int Id, string Name, decimal Price);
record ProductOut(int Id, string Name, string? Description, decimal Price);
record OrderRow(int Id, decimal Total);
record LoginIn(string? Password);
record AdminOrder(int Id, string Customer, string Email, string Phone, string? Address, string? Notes, decimal Total, string Status, DateTime CreatedAt, DateTime? PaidAt, string? Items);
record OrderIn(string FirstName, string LastName, string Email, string Phone, string? Address, string? Notes, List<Item> Items) {
    public string? Validate() {
        if (Items is null || Items.Count is 0 or > 10 || Items.Any(i => i.Qty is < 1 or > 10) || Items.Select(i => i.ProductId).Distinct().Count() != Items.Count) return "Invalid items";
        if (!Ok(FirstName, 60) || !Ok(LastName, 60) || !Ok(Email, 120) || !Ok(Phone, 20)) return "Invalid details";
        if (!MailAddress.TryCreate(Email, out _) || !Phone.All(c => char.IsDigit(c) || c is '+' or ' ')) return "Invalid email or phone";
        if ((Address?.Length ?? 0) > 250 || (Notes?.Length ?? 0) > 500) return "Text too long";
        return null;
    }
    static bool Ok(string? s, int max) => !string.IsNullOrWhiteSpace(s) && s.Length <= max;
}

class PayFast(string id, string key, string pass, bool sandbox) {
    public string Url => sandbox ? "https://sandbox.payfast.co.za" : "https://www.payfast.co.za";
    public string Passphrase => pass;
    static string Enc(string s) => Uri.EscapeDataString(s).Replace("%20", "+").Replace("~", "%7E");
    public Dictionary<string, string> Build(string mref, decimal amount, string item, OrderIn o, string ret, string cancel, string notify) {
        var f = new Dictionary<string, string> {   // PayFast needs this exact field order
            ["merchant_id"] = id, ["merchant_key"] = key, ["return_url"] = ret, ["cancel_url"] = cancel, ["notify_url"] = notify,
            ["name_first"] = o.FirstName.Trim(), ["name_last"] = o.LastName.Trim(), ["email_address"] = o.Email.Trim(),
            ["m_payment_id"] = mref, ["amount"] = amount.ToString("0.00", CultureInfo.InvariantCulture), ["item_name"] = item };
        var s = string.Join("&", f.Where(x => x.Value != "").Select(x => $"{x.Key}={Enc(x.Value)}"));
        if (pass != "") s += "&passphrase=" + Enc(pass);
        f["signature"] = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
        return f;
    }
}

static class Passwords {
    public static string Hash(string pw) {
        var salt = RandomNumberGenerator.GetBytes(16);
        var h = Rfc2898DeriveBytes.Pbkdf2(pw, salt, 210000, HashAlgorithmName.SHA256, 32);
        return $"210000.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(h)}";
    }
    public static bool Verify(string pw, string stored) {
        var p = stored.Split('.');
        if (p.Length != 3 || !int.TryParse(p[0], out var it)) return false;
        var h = Rfc2898DeriveBytes.Pbkdf2(pw, Convert.FromBase64String(p[1]), it, HashAlgorithmName.SHA256, 32);
        return CryptographicOperations.FixedTimeEquals(h, Convert.FromBase64String(p[2]));
    }
}
