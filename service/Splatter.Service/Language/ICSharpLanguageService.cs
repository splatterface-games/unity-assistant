using System.Text.Json;

namespace Splatter.Service.Language;

public interface ICSharpLanguageService : IAsyncDisposable
{
    Task<object> InvokeAsync(string toolName, JsonElement arguments, CancellationToken cancellationToken);
}
