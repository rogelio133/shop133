var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllersWithViews();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

// Se queda, al reves que en Catalog.API y Orders.API. 5.1 se la quito a esos dos porque
// estan DETRAS del Gateway: su 307 devolvia al cliente la direccion real del servicio, que
// es justo lo que la regla 3 existe para impedir. Shop133.Web no lo esta — es la app que
// abre el navegador, no la proxea nadie, y su perfil activo sirve tambien en https.
app.UseHttpsRedirection();
app.UseRouting();

// Aqui iba el app.UseAuthorization() de la plantilla. Se quita: no hay ningun esquema de
// autenticacion registrado detras, asi que el middleware no puede autorizar nada. Vuelve
// en 8.1, cuando el JWT del Gateway le de algo que mirar.

app.MapStaticAssets();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}")
    .WithStaticAssets();

app.Run();
