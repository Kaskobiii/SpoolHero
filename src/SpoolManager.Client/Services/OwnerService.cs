using System.Net.Http.Json;
using SpoolManager.Shared.DTOs.Owners;

namespace SpoolManager.Client.Services;

public class OwnerService(HttpClient http)
{
    public Task<List<OwnerDto>?> GetAllAsync() => http.GetFromJsonAsync<List<OwnerDto>>("api/owners");
    public Task<HttpResponseMessage> CreateAsync(SaveOwnerRequest request) => http.PostAsJsonAsync("api/owners", request);
    public Task<HttpResponseMessage> UpdateAsync(Guid id, SaveOwnerRequest request) => http.PutAsJsonAsync($"api/owners/{id}", request);
}
