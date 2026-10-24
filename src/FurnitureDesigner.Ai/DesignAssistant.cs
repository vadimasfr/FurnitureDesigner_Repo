using System.Text;
using System.Text.Json;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Beta.Messages;
using FurnitureDesigner.Core.Assistant;
using FurnitureDesigner.Core.Editing;
using FurnitureDesigner.Core.Templates;

namespace FurnitureDesigner.Ai;

/// <summary>
/// Chat assistant that changes the open project through <see cref="DesignTools"/> (Claude tool use).
/// Must be called from the UI thread: tools run against the editor between API calls.
/// The conversation is append-only; each request carries the current project state, so undo,
/// manual edits and imports between requests are seen by the model.
/// </summary>
public sealed class DesignAssistant(ProjectEditor editor)
{
    public const string ModelId = "claude-opus-5-5";
    private const int MaxToolRounds = 25;

    private readonly DesignTools _tools = new(editor);
    private readonly List<BetaMessageParam> _history = [];
    private AnthropicClient? _client;

    /// <summary>Raised for each tool call with a short human-readable line.</summary>
    public event Action<string>? ToolCalled;

    /// <summary>Boards changed during the last request.</summary>
    public HashSet<Guid> Affected { get; } = [];

    public void Reset() => _history.Clear();

    /// <summary>
    /// Sends a request and lets Claude run tools until it is done. All changes form one undo step.
    /// Returns Claude's final text.
    /// </summary>
    public async Task<string> SendAsync(string request, CancellationToken cancellationToken)
    {
        // Uses ANTHROPIC_API_KEY, or a profile from `ant auth login`.
        _client ??= new AnthropicClient();
        int historyStart = _history.Count;
        Affected.Clear();

        _history.Add(new BetaUserMessageParam
        {
            Content = $"<project_state>\n{_tools.GetProject(includeBoards: false)}\n</project_state>\n\n{request}",
        });

        var reply = new StringBuilder();
        try
        {
            using var transaction = editor.BeginTransaction();
            for (int round = 0; round < MaxToolRounds; round++)
            {
                BetaMessage response = await _client.Beta.Messages.Create(CreateParams(), cancellationToken);

                if (response.StopReason == "refusal")
                    return AppendRefusal(reply, response);

                var assistantContent = new List<BetaContentBlockParam>();
                var toolResults = new List<BetaContentBlockParam>();
                foreach (var block in response.Content)
                {
                    if (block.TryPickText(out var text))
                    {
                        assistantContent.Add(new BetaTextBlockParam { Text = text.Text });
                        reply.AppendLine(text.Text);
                    }
                    else if (block.TryPickThinking(out var thinking))
                    {
                        assistantContent.Add(new BetaThinkingBlockParam { Thinking = thinking.Thinking, Signature = thinking.Signature });
                    }
                    else if (block.TryPickRedactedThinking(out var redacted))
                    {
                        assistantContent.Add(new BetaRedactedThinkingBlockParam { Data = redacted.Data });
                    }
                    else if (block.TryPickFallback(out var fallback))
                    {
                        assistantContent.Add(new BetaFallbackBlockParam
                        {
                            From = new BetaFallbackInfoParam { Model = fallback.From.Model },
                            To = new BetaFallbackInfoParam { Model = fallback.To.Model },
                            Trigger = JsonSerializer.SerializeToElement(fallback.Trigger),
                        });
                    }
                    else if (block.TryPickToolUse(out var toolUse))
                    {
                        assistantContent.Add(new BetaToolUseBlockParam { ID = toolUse.ID, Name = toolUse.Name, Input = toolUse.Input });
                        toolResults.Add(RunTool(toolUse));
                    }
                }

                _history.Add(new BetaAssistantMessageParam { Content = assistantContent });

                if (response.StopReason != "tool_use")
                {
                    if (response.StopReason == "max_tokens")
                        reply.AppendLine("(The answer was cut off because it reached the length limit.)");
                    return reply.ToString().Trim();
                }

                _history.Add(new BetaUserMessageParam { Content = toolResults });
            }
            return reply.Append("(Stopped after too many steps.)").ToString().Trim();
        }
        catch
        {
            // Drop the unfinished exchange so the next request starts from a consistent history.
            _history.RemoveRange(historyStart, _history.Count - historyStart);
            throw;
        }
    }

    private BetaContentBlockParam RunTool(BetaToolUseBlock toolUse)
    {
        string result;
        bool isError = false;
        try
        {
            result = _tools.Execute(toolUse.Name, toolUse.Input);
            Affected.UnionWith(_tools.LastAffected);
            ToolCalled?.Invoke($"{toolUse.Name}: {Shorten(result)}");
        }
        catch (DesignToolException ex)
        {
            result = ex.Message;
            isError = true;
            ToolCalled?.Invoke($"{toolUse.Name} failed: {ex.Message}");
        }
        return new BetaToolResultBlockParam { ToolUseID = toolUse.ID, Content = result, IsError = isError };
    }

    private static string AppendRefusal(StringBuilder reply, BetaMessage response)
    {
        reply.AppendLine("Claude declined this request.");
        if (response.StopDetails?.Explanation is { Length: > 0 } explanation)
            reply.AppendLine(explanation);
        return reply.ToString().Trim();
    }

    private static string Shorten(string text) => text.Length <= 140 ? text : text[..140] + "…";

    private MessageCreateParams CreateParams() => new()
    {
        Model = ModelId,
        MaxTokens = 16000,
        Betas = ["server-side-fallback-2026-07-01"],
        // Re-serve a policy decline with a fallback model chosen by the API.
        Fallbacks = new Default(),
        Thinking = new BetaThinkingConfigAdaptive(),
        OutputConfig = new BetaOutputConfig { Effort = Effort.Medium },
        System = new List<BetaTextBlockParam>
        {
            new() { Text = SystemPrompt, CacheControl = new BetaCacheControlEphemeral() },
        },
        Tools = ToolDefinitions,
        Messages = _history,
    };

    private static readonly List<BetaToolUnion> ToolDefinitions = DesignTools.Definitions
        .Select(d => (BetaToolUnion)new BetaTool
        {
            Name = d.Name,
            Description = d.Description,
            Strict = true,
            InputSchema = InputSchema.FromRawUnchecked(
                JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(d.InputSchema.ToJsonString())!),
        })
        .ToList();

    private static readonly string SystemPrompt = BuildSystemPrompt();

    private static string BuildSystemPrompt()
    {
        var sb = new StringBuilder("""
            You are the design assistant inside a furniture design program similar to PRO100.
            The user describes changes to their room and furniture; you make them with the tools, then reply
            with a short summary of what you changed (sizes in mm). Answer in the user's language.

            Coordinates are millimetres. X goes right along the back wall, Y goes up, Z goes from the back
            wall (Z = 0) towards the front of the room. The room spans 0..width in X and 0..depth in Z.
            Every board is an axis-aligned box given by its minimum corner and its size.
            An item (cabinet, wardrobe...) is a group of boards; its position is the back-left-bottom corner
            of its bounding box. Doors and drawer fronts stick out 18 mm in front of the carcass.

            Each user message starts with the current project state; ids in it are current, earlier ids may be gone.
            Use get_project with include_boards when you need individual boards.
            Keep furniture inside the room and avoid overlapping items unless asked. Kitchen worktops usually sit
            at 820 mm on base cabinets, wall cabinets usually start around 1450 mm.
            If a request is ambiguous in a way that matters (which item, which wall), ask instead of guessing.
            Changes are applied immediately; the user can undo the whole request with one Ctrl+Z.

            Templates (key: name, defaults W×H×D, elevation, plinth, shelves/doors/drawers):
            """);
        foreach (var t in TemplateLibrary.All)
        {
            var d = t.Defaults;
            sb.AppendLine($"- {t.Key}: {t.Name}, {d.Width}×{d.Height}×{d.Depth}, elevation {d.Elevation}, plinth {d.Plinth}, " +
                          $"{d.Shelves}/{d.Doors}/{d.Drawers}{(t.IsCabinet ? "" : " (single board)")}");
        }
        return sb.ToString();
    }

    /// <summary>Short description of an API failure for the chat panel.</summary>
    public static string DescribeError(Exception ex) => ex switch
    {
        AnthropicUnauthorizedException => "Not authorised. Set the ANTHROPIC_API_KEY environment variable (or run `ant auth login`) and restart the program.",
        AnthropicRateLimitException => "Rate limited by the API. Wait a moment and try again.",
        AnthropicApiException api => $"API error: {api.Message}",
        OperationCanceledException => "Cancelled.",
        HttpRequestException http => $"Network error: {http.Message}",
        _ => $"{ex.GetType().Name}: {ex.Message}",
    };
}
