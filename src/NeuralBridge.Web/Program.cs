using NeuralBridge.Application;
using NeuralBridge.Infrastructure;
using NeuralBridge.Web.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

// Secrets: user secrets (Development, loaded automatically), environment variables
// (Authentication__Google__ClientSecret), and key-per-file secrets, i.e. one file per key such as
// /run/secrets/Authentication__Google__ClientSecret (Docker/Kubernetes secrets, or files written by a vault agent).
var secretsPath = Environment.GetEnvironmentVariable("NEURALBRIDGE_SECRETS_PATH") ?? "/run/secrets";
if (Directory.Exists(secretsPath))
{
    builder.Configuration.AddKeyPerFile(secretsPath, optional: true);
}

builder.Services.AddNeuralBridgeApplication(builder.Configuration);
builder.Services.AddNeuralBridgeInfrastructure(builder.Configuration);
builder.Services.AddNeuralBridgeWeb(builder.Configuration, builder.Environment);

var app = builder.Build();
app.UseNeuralBridgePipeline();
await app.RunAsync();

/// <summary>Entry point marker (also used by WebApplicationFactory-based tests).</summary>
public partial class Program;
