using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NeuralBridge.Application.Abstractions;
using NeuralBridge.Application.Options;
using NeuralBridge.Domain.Sessions;

namespace NeuralBridge.Application.Sessions;

/// <summary>Draws codes until one is not in use. With 60 bits of entropy a retry is astronomically rare but still handled.</summary>
public sealed class SessionCodeAllocator
{
    private readonly ISessionCodeGenerator _codes;
    private readonly SessionOptions _options;
    private readonly ILogger<SessionCodeAllocator> _logger;

    public SessionCodeAllocator(ISessionCodeGenerator codes, IOptions<SessionOptions> options, ILogger<SessionCodeAllocator> logger)
    {
        _codes = codes;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<SessionCode> AllocateAsync(ISessionUnitOfWork uow, CancellationToken cancellationToken)
    {
        for (var i = 0; i < _options.CodeGenerationMaxAttempts; i++)
        {
            var candidate = _codes.Generate();
            if (!await uow.CodeInUseAsync(candidate.Value, cancellationToken))
            {
                return candidate;
            }

            _logger.LogInformation("Generated join code already in use; drawing another");
        }

        throw new InvalidOperationException("Could not allocate a unique session code.");
    }
}

public static class PinPolicy
{
    public static string? Normalize(string? pin) =>
        string.IsNullOrWhiteSpace(pin) ? null : pin.Trim();

    /// <summary>Returns a user-facing error, or <c>null</c> when the (normalized) PIN is acceptable.</summary>
    public static string? Validate(string? pin, SessionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (pin is null)
        {
            return null;
        }

        if (pin.Length < options.PinMinLength || pin.Length > options.PinMaxLength)
        {
            return $"PINs must be {options.PinMinLength}–{options.PinMaxLength} characters.";
        }

        return pin.Any(char.IsControl) ? "PINs can't contain control characters." : null;
    }
}
