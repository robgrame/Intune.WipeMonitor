using System.ComponentModel.DataAnnotations;
using Intune.WipeMonitor.Shared;

namespace Intune.WipeMonitor.Models;

/// <summary>
/// Record di cleanup per un device con wipe completato.
/// Persistito in database per tracciamento e storico.
/// </summary>
public class DeviceCleanupRecord
{
    /// <summary>ID audit dell'azione di wipe da Graph API.</summary>
    [Key]
    public string WipeActionId { get; set; } = string.Empty;

    /// <summary>Nome visualizzato del device (es. FC1DSK005).</summary>
    [Required, MaxLength(256)]
    public string DeviceDisplayName { get; set; } = string.Empty;

    /// <summary>ID del managed device in Intune.</summary>
    [Required, MaxLength(128)]
    public string ManagedDeviceId { get; set; } = string.Empty;

    /// <summary>UPN dell'utente che ha richiesto il wipe.</summary>
    [MaxLength(256)]
    public string InitiatedBy { get; set; } = string.Empty;

    /// <summary>UPN del proprietario del device.</summary>
    [MaxLength(256)]
    public string DeviceOwner { get; set; } = string.Empty;

    /// <summary>Stato corrente dell'azione di wipe (pending, done, failed).</summary>
    [Required, MaxLength(50)]
    public string WipeActionState { get; set; } = string.Empty;

    /// <summary>Timestamp della richiesta di wipe.</summary>
    public DateTimeOffset WipeRequestedAt { get; set; }

    /// <summary>Timestamp di quando il wipe è stato rilevato come completato.</summary>
    public DateTimeOffset? WipeCompletedAt { get; set; }

    /// <summary>Stato complessivo del processo di cleanup.</summary>
    public CleanupStatus Status { get; set; } = CleanupStatus.WipePending;

    /// <summary>UPN dell'operatore che ha approvato il cleanup.</summary>
    [MaxLength(256)]
    public string? ApprovedBy { get; set; }

    /// <summary>Timestamp dell'approvazione.</summary>
    public DateTimeOffset? ApprovedAt { get; set; }

    /// <summary>Timestamp di completamento del cleanup.</summary>
    public DateTimeOffset? CleanupCompletedAt { get; set; }

    /// <summary>Note aggiuntive dell'operatore.</summary>
    [MaxLength(2000)]
    public string? Notes { get; set; }

    /// <summary>Data di prima rilevazione.</summary>
    public DateTimeOffset FirstSeenAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Ultimo aggiornamento.</summary>
    public DateTimeOffset LastUpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Step di cleanup associati.</summary>
    public List<CleanupStepLog> CleanupSteps { get; set; } = [];
}

/// <summary>
/// Log di un singolo step di cleanup (AD, SCCM, Intune).
/// </summary>
public class CleanupStepLog
{
    public int Id { get; set; }

    /// <summary>FK verso DeviceCleanupRecord.</summary>
    [Required]
    public string WipeActionId { get; set; } = string.Empty;

    /// <summary>Target del cleanup.</summary>
    public CleanupTarget Target { get; set; }

    /// <summary>Esito dello step.</summary>
    public StepResult Result { get; set; } = StepResult.Pending;

    /// <summary>Messaggio di errore in caso di fallimento.</summary>
    [MaxLength(4000)]
    public string? ErrorMessage { get; set; }

    /// <summary>Timestamp di esecuzione.</summary>
    public DateTimeOffset ExecutedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Navigazione verso il record padre.</summary>
    public DeviceCleanupRecord? DeviceCleanupRecord { get; set; }
}
