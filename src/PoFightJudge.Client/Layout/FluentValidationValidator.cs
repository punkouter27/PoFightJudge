using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;

namespace PoFightJudge.Client.Layout;

/// <summary>
/// Wires a Shared FluentValidation validator into Blazor's <see cref="EditContext"/>, so a
/// <c>RadzenTemplateForm</c> shows the server's own rules as inline field errors (<c>ValidationMessage</c>) and refuses
/// to submit while any fail. The fleet's pattern (PoTraffic), with one addition: the form edits a mutable
/// <typeparamref name="TModel"/> while the rules are written for the immutable request record it becomes, so
/// <see cref="Project"/> turns one into the other and failures map back by property name. <see cref="Show"/> puts a
/// 400's field errors next to their fields the same way.
/// </summary>
public sealed class FluentValidationValidator<TModel, TRequest> : ComponentBase, IDisposable
    where TModel : class
{
    private ValidationMessageStore? _messages;
    private EditContext? _subscribed;

    [CascadingParameter] private EditContext? CurrentEditContext { get; set; }

    [Parameter, EditorRequired] public IValidator<TRequest> Validator { get; set; } = default!;

    [Parameter, EditorRequired] public Func<TModel, TRequest> Project { get; set; } = default!;

    protected override void OnInitialized()
    {
        if (CurrentEditContext is null)
        {
            throw new InvalidOperationException($"{nameof(FluentValidationValidator<TModel, TRequest>)} goes inside a form.");
        }

        _subscribed = CurrentEditContext;
        _messages = new ValidationMessageStore(_subscribed);
        _subscribed.OnValidationRequested += OnValidationRequested;
        _subscribed.OnFieldChanged += OnFieldChanged;
    }

    /// <summary>A validation problem from the API, keyed by the request's property names, shown under the fields.</summary>
    public void Show(IReadOnlyDictionary<string, string[]> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        if (_subscribed is null || _messages is null)
        {
            return;
        }

        _messages.Clear();
        foreach (var (field, messages) in errors)
        {
            _messages.Add(_subscribed.Field(field), messages);
        }

        _subscribed.NotifyValidationStateChanged();
    }

    private void OnValidationRequested(object? sender, ValidationRequestedEventArgs e)
    {
        if (_subscribed is null || _messages is null || _subscribed.Model is not TModel model)
        {
            return;
        }

        _messages.Clear();
        Add(Validator.Validate(Project(model)), field: null);
        _subscribed.NotifyValidationStateChanged();
    }

    private void OnFieldChanged(object? sender, FieldChangedEventArgs e)
    {
        if (_subscribed is null || _messages is null || _subscribed.Model is not TModel model)
        {
            return;
        }

        _messages.Clear(e.FieldIdentifier);
        Add(Validator.Validate(Project(model), o => o.IncludeProperties(e.FieldIdentifier.FieldName)), e.FieldIdentifier);
        _subscribed.NotifyValidationStateChanged();
    }

    private void Add(ValidationResult result, FieldIdentifier? field)
    {
        foreach (var failure in result.Errors)
        {
            _messages!.Add(field ?? _subscribed!.Field(failure.PropertyName), failure.ErrorMessage);
        }
    }

    public void Dispose()
    {
        if (_subscribed is null)
        {
            return;
        }

        _subscribed.OnValidationRequested -= OnValidationRequested;
        _subscribed.OnFieldChanged -= OnFieldChanged;
    }
}
