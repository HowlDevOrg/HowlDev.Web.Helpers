using HowlDev.Web.Helpers.WebSockets;

var builder = WebApplication.CreateBuilder(args);

builder.AddWebSocketService<int>();
builder.AddWebSocketService<string>();

var app = builder.Build();
app.UseWebSockets();

app.Map("/ws/{id}", async (WebSocketService<int> service, HttpContext context, int id) => {
    await service.RegisterSocket(context, id);
});
app.Map("/ws/string/{id}", async (WebSocketService<string> service, HttpContext context, string id) => {
    await service.RegisterSocket(context, id);
});

app.MapGet("/post/{id}", async (WebSocketService<int> service, int id) => {
    await service.SendSocketMessage(id, $"This is the message: coming from id {id} at time {DateTime.Now}");
});
app.MapGet("/post/string/{id}", async (WebSocketService<string> service, string id) => {
    await service.SendSocketMessage(id, $"This is the message: coming from id {id} at time {DateTime.Now}");
});

app.Run();
