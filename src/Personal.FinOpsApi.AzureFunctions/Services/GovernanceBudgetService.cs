using System.Globalization;
using Personal.FinOpsApi.AzureFunctions.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Personal.FinOpsApi.AzureFunctions.Services;

public sealed class GovernanceBudgetService(
    IGovernanceBudgetRepository repository,
    ICostExportVersionStore versions,
    GovernanceDataHealthService health,
    IConfiguration configuration,
    ILogger<GovernanceBudgetService> logger,
    IGovernanceAuditLogRepository audit)
{
    public Task<IReadOnlyList<GovernanceBudget>> ListAsync(CancellationToken cancellationToken = default) =>
        repository.ListAsync(cancellationToken);

    public Task<GovernanceBudget> SaveAsync(string id, GovernanceBudgetInput input, string actor,
        DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var definition = Validate(input);
        if (!Guid.TryParseExact(id, "D", out _)) throw new ArgumentException("ID de orçamento inválido.");
        return MutateAsync(id, input.Version is null ? "Create" : "Update", input.Version,
            definition, actor, now,
            () => repository.SaveAsync(new(id, definition with { Version = null }, now, actor, ""),
                input.Version, cancellationToken), cancellationToken);
    }

    public Task DeleteAsync(string id, string version, string actor, DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParseExact(id, "D", out _)) throw new ArgumentException("ID de orçamento inválido.");
        if (version is null) throw new ArgumentException("Versão obrigatória para excluir.");
        _ = BlobGovernanceBudgetRepository.WriteConditions(version);
        return MutateAsync<object?>(id, "Delete", version, null, actor, now, async () =>
        {
            await repository.DeleteAsync(id, version, cancellationToken);
            return null;
        }, cancellationToken);
    }

    private async Task<T> MutateAsync<T>(string id, string action, string? version, GovernanceBudgetInput? parameters,
        string actor, DateTimeOffset now, Func<Task<T>> mutate, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(actor) || actor.Length > 256)
            throw new ArgumentException("Identificador do ator autenticado obrigatório.");
        var operationId = Guid.NewGuid().ToString("D");
        GovernanceBudget? before = null;
        AuditLogDocument Event(string outcome, DateTimeOffset timestamp, object? after = null) => new()
        {
            EntityType = "governanceBudget", EntityId = id, Action = action,
            PerformedBy = actor, PerformedAt = timestamp.UtcDateTime, Before = before, After = after,
            Metadata = new() { ["operationId"] = operationId, ["outcome"] = outcome,
                ["expectedVersion"] = version ?? "", ["parameters"] = (object?)parameters ?? new { id, version },
                ["requestedAt"] = now.UtcDateTime }
        };

        // Fail closed: no mutation is attempted unless its intent is durable.
        try { await audit.AppendAsync(Event("Intent", now), cancellationToken); }
        catch (Exception ex) { throw new GovernanceAuditIncompleteException(operationId, false, ex); }
        T result;
        try
        {
            if (version is not null)
            {
                before = await repository.GetAsync(id, cancellationToken);
                if (before is null) throw new Azure.RequestFailedException(404, "Missing budget", "BlobNotFound", null);
                if (before.Version != version) throw new Azure.RequestFailedException(412, "ETag mismatch");
            }
            // Repository CAS remains authoritative if another request writes after the preceding read.
            result = await mutate();
        }
        catch (Exception ex)
        {
            var rejected = ex is Azure.RequestFailedException { Status: 404 or 409 or 412 };
            var terminal = Event(rejected ? "Rejected" : "Indeterminate", DateTimeOffset.UtcNow);
            terminal.Metadata!["errorType"] = ex.GetType().Name; // No credentials or raw provider error text.
            if (ex is Azure.RequestFailedException azure) terminal.Metadata["storageStatus"] = azure.Status;
            await CompleteAuditAsync(terminal, operationId, rejected ? false : null);
            if (!rejected) throw new GovernanceAuditIncompleteException(operationId, null, ex);
            throw;
        }
        await CompleteAuditAsync(Event("Succeeded", DateTimeOffset.UtcNow, result), operationId, true);
        return result;
    }

    private async Task CompleteAuditAsync(AuditLogDocument document, string operationId, bool? applied)
    {
        // Request disconnect/cancellation must not suppress a terminal event; keep recovery IO bounded.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try { await audit.AppendAsync(document, timeout.Token); }
        catch (Exception ex)
        {
            logger.LogError(ex, "Auditoria pendente para operação de orçamento {OperationId}; applied={Applied}", operationId, applied);
            throw new GovernanceAuditIncompleteException(operationId, applied, ex);
        }
    }

    public static GovernanceBudgetInput Validate(GovernanceBudgetInput input)
    {
        if (string.IsNullOrWhiteSpace(input.Name) || input.Name.Trim().Length > 120)
            throw new ArgumentException("Nome obrigatório, até 120 caracteres.");
        if (!Guid.TryParse(input.SubscriptionId, out var subscription)) throw new ArgumentException("Subscription inválida.");
        if (!DateOnly.TryParseExact(input.Month + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var month) || month.Year < 2000 || month.Year > 2100)
            throw new ArgumentException("Mês inválido; informe yyyy-MM, entre 2000 e 2100.");
        if (input.Amount <= 0 || input.Amount > 1_000_000_000_000m || decimal.Round(input.Amount, 2) != input.Amount)
            throw new ArgumentException("Valor deve ser positivo, até 1 trilhão, com no máximo duas casas decimais.");
        var currency = input.Currency?.Trim().ToUpperInvariant();
        if (currency is null || currency.Length != 3 || currency.Any(c => c is < 'A' or > 'Z'))
            throw new ArgumentException("Moeda deve conter três letras; não há conversão cambial.");
        if (input.CostCenter is not null && (string.IsNullOrWhiteSpace(input.CostCenter) || input.CostCenter.Trim().Length > 200))
            throw new ArgumentException("Centro de custo deve ter entre 1 e 200 caracteres, ou ser null.");
        _ = BlobGovernanceBudgetRepository.WriteConditions(input.Version);
        return input with { Name = input.Name.Trim(), SubscriptionId = subscription.ToString("D"),
            Currency = currency, CostCenter = input.CostCenter?.Trim() };
    }

    public async Task<GovernanceBudgetEvaluation?> EvaluateAsync(string id, DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParseExact(id, "D", out _)) throw new ArgumentException("ID de orçamento inválido.");
        var budget = await repository.GetAsync(id, cancellationToken);
        if (budget is null) return null;
        var definition = Validate(budget.Definition);
        var start = DateOnly.ParseExact(definition.Month + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var end = start.AddMonths(1).AddDays(-1);
        var consolidated = DateOnly.FromDateTime(now.UtcDateTime).AddDays(-health.DelayDays);
        var to = consolidated < end ? consolidated : end;
        var dates = GovernanceDataHealthService.Dates(start, to).ToArray();
        var mode = health.PublicationMode(definition.SubscriptionId);
        var warnings = new List<string>
        {
            "Projeção linear estimada: média dos dias observados × dias do mês. Não é garantia nem previsão estatística; não envia notificações."
        };
        if (mode == CostExportPublicationModes.Shadow) warnings.Add("Shadow: custos observados ainda não promovidos a oficiais.");
        if (definition.CostCenter is not null)
            warnings.Add("Centro de custo considera somente atribuição/tag do snapshot importado; ajustes manuais de rateio e ownership não são aplicados.");
        CostExportRevisionIndex? index = null;
        if (dates.Length > 0)
        {
            try { index = await versions.LoadIndexAsync(definition.SubscriptionId, mode, cancellationToken); }
            catch (Exception ex) when (GovernanceDataHealthService.IsEvidenceFailure(ex))
            {
                logger.LogWarning(ex, "Índice indisponível na avaliação de {BudgetId}", id);
                warnings.Add("Não foi possível ler o índice de custos.");
            }
        }
        if (index is null && dates.Length > 0) warnings.Add("Sem índice versionado; custos legados não foram presumidos nem consultados por fallback.");

        decimal total = 0;
        var observedDays = 0;
        DateOnly? dataAsOf = null;
        var missing = new List<string>();
        var attributionComplete = true;
        foreach (var date in dates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (index is null || !index.Days.TryGetValue(date, out var reference))
            {
                missing.Add(date);
                continue;
            }
            try
            {
                var snapshot = await versions.LoadSnapshotAsync(definition.SubscriptionId, mode, reference.Revision, cancellationToken);
                if (!string.Equals(snapshot.SubscriptionId, definition.SubscriptionId, StringComparison.OrdinalIgnoreCase)
                    || snapshot.PublicationMode != mode || snapshot.Date.ToString("yyyy-MM-dd") != date
                    || snapshot.CostBasis != "ActualCost" || snapshot.Revision != reference.Revision)
                    throw new InvalidDataException("Snapshot não corresponde à referência financeira solicitada.");
                var (amount, complete) = ReadAmount(snapshot, definition);
                attributionComplete &= complete;
                if (amount is null) { missing.Add(date); continue; }
                total += amount.Value;
                observedDays++;
                dataAsOf = DateOnly.ParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            }
            catch (Exception ex) when (GovernanceDataHealthService.IsEvidenceFailure(ex))
            {
                logger.LogWarning(ex, "Snapshot indisponível na avaliação de {BudgetId}, {Date}", id, date);
                missing.Add(date);
                warnings.Add($"Snapshot de {date} não pôde ser validado/lido.");
            }
        }
        decimal? observed = observedDays == 0 ? null : total;
        decimal? percentage = observed is null ? null : Math.Round(observed.Value / definition.Amount * 100, 2);
        decimal? forecast = observed is null ? null : Math.Round(observed.Value / observedDays * end.Day, 2);
        var reliable = dates.Length > 0 && missing.Count == 0 && attributionComplete && observedDays >= 7
            && mode == CostExportPublicationModes.Cutover;
        if (missing.Count > 0) warnings.Add("Cobertura incompleta: consumo é parcial; projeção não confiável. Ausência de dados/moeda não equivale a zero.");
        if (!attributionComplete) warnings.Add("Atribuição incompleta ou custo sem recurso/tag; o total do centro pode estar subestimado.");
        if (observedDays < 7) warnings.Add("Menos de sete dias observados: projeção sem base mínima de confiança.");
        if (dates.Length == 0) warnings.Add("Período ainda sem dias consolidados esperados.");
        return new(budget, mode, observed, percentage, forecast, reliable,
            forecast is null ? null : forecast > definition.Amount,
            observed is null ? [] : new[] { 50, 80, 100 }.Where(t => observed.Value >= definition.Amount * t / 100).ToArray(),
            dates.Length, observedDays, missing, dataAsOf, warnings);
    }

    private (decimal? Amount, bool Complete) ReadAmount(CostExportDailySnapshot snapshot, GovernanceBudgetInput definition)
    {
        bool Currency(string? value) => string.Equals(value, definition.Currency, StringComparison.OrdinalIgnoreCase);
        if (snapshot.IsTombstone) return Currency(snapshot.TombstoneCurrency) ? (0m, true) : (null, false);
        var services = snapshot.ByService.Where(row => Currency(row.Currency)).ToArray();
        if (services.Length == 0) return (null, false);
        if (definition.CostCenter is null) return (services.Sum(row => row.TotalCost), true);
        var tagName = configuration["CostAllocationTagName"] ?? "CentrodeCusto";
        var resources = snapshot.ByResource.Where(row => Currency(row.Currency)).Select(row => new
        {
            Row = row,
            Center = string.IsNullOrWhiteSpace(row.CostCenter) ? CostTagParser.TryGetTagValue(row.Tags, tagName) : row.CostCenter
        }).ToArray();
        var matching = resources.Where(row => string.Equals(row.Center?.Trim(), definition.CostCenter, StringComparison.OrdinalIgnoreCase)).ToArray();
        var complete = resources.Length > 0 && resources.All(row => !string.IsNullOrWhiteSpace(row.Center))
            && resources.Sum(row => row.Row.TotalCost) == services.Sum(row => row.TotalCost)
            && !snapshot.Allocation.Any(row => Currency(row.Currency) && row.UnallocatedCost != 0);
        // No match can only mean zero if all costs are attributed; otherwise it is unknown.
        return matching.Length > 0 || complete ? (matching.Sum(row => row.Row.TotalCost), complete) : (null, false);
    }
}