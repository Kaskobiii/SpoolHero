// Run with: dotnet run --project tests/MaterialColorFlowTests
// Exercises the real form and HTTP service without external packages or a database.
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using SpoolManager.Client.Pages.Materials;
using SpoolManager.Client.Services;
using SpoolManager.Shared.DTOs.Materials;
using SpoolManager.Shared.DTOs.Projects;

var handler = new MaterialApi();
var http = new HttpClient(handler) { BaseAddress = new Uri("https://test.invalid/") };
var js = new NoOpJs();
var project = new ProjectService(http, js);
await project.SwitchProjectAsync(new ProjectDto { Id = Guid.NewGuid(), Name = "Test project" });
var nav = new TestNavigation();
var form = new MaterialForm();
SetProperty(form, "Materials", new MaterialService(http));
SetProperty(form, "Project", project);
SetProperty(form, "L", new LocalizationService(http, js));
SetProperty(form, "Nav", nav);
SetProperty(form, "Id", handler.Source.Id);

await Load("materials/" + handler.Source.Id + "/color-variant");
var color = Current();
Require(color.ColorHex == "FFFFFF" && color.ColorName == null, "A variant must start with a fresh color.");
color.ColorHex = "#ff6400";
color.ColorName = "Orange";
await Save(true);
Require(handler.Writes.Count == 1 && handler.Writes[0].Method == "POST", "A variant must create a new material.");
var first = handler.Writes[0].Body;
Require(first.GetProperty("colorHex").GetString() == "FF6400", "Hex colors must be normalized.");
Require(first.GetProperty("colorName").GetString() == "Orange", "The chosen color must be saved.");
foreach (var property in typeof(CreateMaterialRequest).GetProperties().Where(p => p.Name is not "ColorHex" and not "ColorName"))
{
    var expected = typeof(FilamentMaterialDto).GetProperty(property.Name)!.GetValue(handler.Source);
    Require(Equals(property.GetValue(color), expected), "Shared setting was not copied: " + property.Name);
    var jsonName = JsonNamingPolicy.CamelCase.ConvertName(property.Name);
    Require(first.GetProperty(jsonName).ToString() == JsonSerializer.SerializeToElement(expected).ToString(),
        "Shared setting was not sent: " + property.Name);
}
Require(!first.TryGetProperty("id", out _) && !first.TryGetProperty("ofdVariantId", out _)
    && !first.TryGetProperty("ofdFilamentId", out _), "A new color cannot inherit database identities.");
Require(color.ColorHex == "FFFFFF" && color.ColorName == null, "Save and next must clear the previous color.");
Require(nav.Uri.EndsWith("/color-variant"), "Save and next must stay in the editor.");

color.ColorHex = "12345Z";
await Save(false);
Require(handler.Writes.Count == 1, "Invalid colors must not be submitted.");
color.ColorHex = "3344FF";
color.ColorName = "Blue";
handler.FailNextWrite = true;
await Save(true);
Require(color.ColorHex == "3344FF" && color.ColorName == "Blue", "A failed save must retain the entered color.");
Require((bool)Field(form, "_saving")! == false, "A failed save must re-enable the form.");
await Save(false);
Require(handler.Writes[^1].Method == "POST" && nav.Uri.EndsWith("/materials"), "The last color must create and return to the list.");

await Load("materials/" + handler.Source.Id + "/edit");
Require(Current().ColorHex == handler.Source.ColorHex && Current().ColorName == handler.Source.ColorName,
    "Editing must keep the original color.");
await Save(false);
Require(handler.Writes[^1].Method == "PUT", "Editing must update the existing material.");

// Blazor can retain an omitted route parameter when reusing a component instance.
// Starting a new material after editing must still POST, even when Id remains set.
await Load("materials/new");
Current().Type = "PETG";
Current().Brand = "New material";
await Save(false);
Require(handler.Writes[^1].Method == "POST", "The new-material route must never update a retained Id.");

handler.FailLoad = true;
var beforeFailedLoad = handler.Writes.Count;
await Load("materials/" + handler.Source.Id + "/edit");
await Save(false);
Require(handler.Writes.Count == beforeFailedLoad, "A failed load must not permit saving a blank update.");
Console.WriteLine("Material color flow checks passed: copy, shared properties, identity, repeated saves, failure handling, editing, and route transitions.");

async Task Load(string route)
{
    nav.NavigateTo(route);
    await (Task)typeof(MaterialForm).GetMethod("OnParametersSetAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, null)!;
}
Task Save(bool next) => (Task)typeof(MaterialForm).GetMethod("SaveAsync", BindingFlags.Instance | BindingFlags.NonPublic,
    [typeof(bool)])!.Invoke(form, [next])!;
CreateMaterialRequest Current() => (CreateMaterialRequest)Field(form, "_form")!;
static object? Field(object target, string name) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target);
static void SetProperty(object target, string name, object value) => target.GetType()
    .GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.SetValue(target, value);
static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

sealed class MaterialApi : HttpMessageHandler
{
    public FilamentMaterialDto Source { get; } = new()
    {
        Id = Guid.NewGuid(), Brand = "Example", Type = "PLA+", ColorHex = "AABBCC", ColorName = "Original",
        MinTempCelsius = 195, MaxTempCelsius = 225, DiameterMm = 2.85m, WeightGrams = 750,
        BedTempCelsius = 60, DensityGCm3 = 1.24m, DryTempCelsius = 45, DryTimeHours = 6,
        Notes = "Shared settings", ReorderUrl = "https://example.invalid/material", PricePerKg = 24.50m,
        IsPublic = true, OfdFilamentId = "external-material", OfdVariantId = "external-color"
    };
    public List<(string Method, JsonElement Body)> Writes { get; } = [];
    public bool FailNextWrite { get; set; }
    public bool FailLoad { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Method == HttpMethod.Get)
            return FailLoad ? new(HttpStatusCode.NotFound) : new(HttpStatusCode.OK) { Content = JsonContent.Create(Source) };
        Writes.Add((request.Method.Method, JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync(cancellationToken))));
        if (FailNextWrite)
        {
            FailNextWrite = false;
            return new(HttpStatusCode.BadRequest) { Content = new StringContent("Please retry") };
        }
        return new(HttpStatusCode.Created) { Content = JsonContent.Create(new { id = Guid.NewGuid() }) };
    }
}

sealed class TestNavigation : NavigationManager
{
    public TestNavigation() => Initialize("https://test.invalid/", "https://test.invalid/materials");
    protected override void NavigateToCore(string uri, bool forceLoad)
    {
        Uri = ToAbsoluteUri(uri).ToString();
        NotifyLocationChanged(false);
    }
}

sealed class NoOpJs : IJSRuntime
{
    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => ValueTask.FromResult(default(TValue)!);
    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => ValueTask.FromResult(default(TValue)!);
}
