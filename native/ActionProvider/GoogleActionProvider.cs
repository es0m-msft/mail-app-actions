using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Windows.AI.Actions;
using Windows.AI.Actions.Provider;
using Windows.Foundation;

namespace EdgeGoogleActions.ActionProvider;

// The current attribute generator requires an input parameter even for read-only actions.
[Guid("51971F35-3D52-43DE-A739-FDC87BAE3D90")]
public partial class GoogleActionProvider : IActionProvider
{
    public IAsyncAction InvokeAsync(ActionInvocationContext context) => InvokeCoreAsync(context).AsAsyncAction();

    private static async Task InvokeCoreAsync(ActionInvocationContext context)
    {
        JsonObject result;
        try
        {
            var inputs = context.GetInputEntities().ToDictionary(
                input => input.Name,
                input => input.Entity is TextActionEntity text
                    ? text.Text : throw new ArgumentException("Only Text input entities are accepted."),
                StringComparer.Ordinal);
            string Required(string name) => inputs.TryGetValue(name, out var value)
                ? value : throw new ArgumentException($"Missing input: {name}");
            var (action, args) = context.ActionId switch
            {
                "EdgeGoogleActions.ListContacts" => ("contacts.list", new JsonObject()),
                "EdgeGoogleActions.ListMeetings" => ("meetings.list", new JsonObject()),
                "EdgeGoogleActions.SendEmail" => ("mail.send", new JsonObject
                {
                    ["to"] = new JsonArray(Required("To")),
                    ["subject"] = Required("Subject"), ["body"] = Required("Body")
                }),
                "EdgeGoogleActions.MoveMeeting" => ("meetings.move", new JsonObject
                {
                    ["eventId"] = Required("EventId"), ["start"] = Required("Start"), ["end"] = Required("End")
                }),
                _ => throw new ArgumentException("Unknown Windows action ID.")
            };
            result = await BridgeClient.InvokeAsync(action, args);
        }
        catch (Exception error) when (error is System.IO.IOException or System.IO.InvalidDataException or
            System.Text.Json.JsonException or InvalidOperationException or UnauthorizedAccessException or ArgumentException)
        {
            System.Diagnostics.Trace.TraceError(error.ToString());
            result = Wire.Error(error.Message);
        }
        context.SetOutputEntity("Json", context.EntityFactory.CreateTextEntity(result.ToJsonString()));
    }
}
