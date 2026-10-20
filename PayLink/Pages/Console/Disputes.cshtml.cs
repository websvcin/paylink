using Microsoft.AspNetCore.Mvc;
using PayLink.Payments;

namespace PayLink.Pages.Console;

public sealed class DisputesModel(PaymentService payments, ConnectorRegistry registry) : ConsolePage
{
    public IReadOnlyList<Dispute> Disputes { get; private set; } = [];
    public string ConnectorName(string id) => registry.Get(id)?.Manifest.Name ?? id;

    public async Task OnGetAsync() => Disputes = await payments.DisputesAsync(Tenant, Livemode);

    public async Task<IActionResult> OnPostNoteAsync(string id, string? note)
    {
        if (!CanOperate) return NotAllowed("note disputes");
        return await payments.NoteDisputeAsync(Tenant, id, note, Me) ? Done("Note saved.") : Failed("Dispute not found.");
    }
}
