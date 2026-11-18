using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

public class MvcRoutingTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory factory;
    public MvcRoutingTests(ApiFactory factory) => this.factory = factory;

    [Fact]
    public void Business_routes_are_discovered_as_Mvc_controller_actions()
    {
        var provider = factory.Services.GetService<IActionDescriptorCollectionProvider>();
        Assert.NotNull(provider);
        var actions = provider.ActionDescriptors.Items.OfType<ControllerActionDescriptor>().ToArray();
        foreach (var controller in new[] { "Auth", "Students", "Departments", "Documents", "Reports", "Users", "Audit", "Dashboard", "Security", "Operations", "Health" })
            Assert.Contains(actions, action => action.ControllerName == controller);
        Assert.Contains(actions, action => action.ControllerName == "Students" && action.AttributeRouteInfo?.Template == "api/students/{id}");
    }
    [Theory]
    [InlineData("{")]
    [InlineData("")]
    public async Task Invalid_json_body_returns_safe_400_with_trace_id(string body)
    {
        var client = factory.Client();
        var csrf = await client.GetFromJsonAsync<System.Text.Json.Nodes.JsonObject>("/api/auth/csrf");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", csrf!["token"]!.ToString());
        var login = await client.PostAsJsonAsync("/api/auth/login", new { email = "admin@campus.local", password = ApiFactory.Password });
        login.EnsureSuccessStatusCode();
        csrf = await client.GetFromJsonAsync<System.Text.Json.Nodes.JsonObject>("/api/auth/csrf");
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", csrf!["token"]!.ToString());
        var response = await client.PostAsync("/api/students", new StringContent(body, System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<System.Text.Json.Nodes.JsonObject>();
        Assert.NotNull(problem!["traceId"]);
        Assert.DoesNotContain("Exception", await response.Content.ReadAsStringAsync());
    }}
