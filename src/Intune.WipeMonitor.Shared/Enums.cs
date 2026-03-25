namespace Intune.WipeMonitor.Shared;

/// <summary>Tipo di repository da cui rimuovere il device.</summary>
public enum CleanupTarget
{
    ActiveDirectory,
    SCCM,
    Intune
}

/// <summary>Esito di un singolo step di cleanup.</summary>
public enum StepResult
{
    Pending,
    Success,
    Failed,
    Skipped,
    NotFound,
    /// <summary>Il SID del device non corrisponde tra AD, SCCM e/o Entra ID.</summary>
    SidMismatch
}

/// <summary>Stato complessivo del processo di cleanup per un device.</summary>
public enum CleanupStatus
{
    /// <summary>Wipe rilevato ma non ancora completato (actionState != done).</summary>
    WipePending,

    /// <summary>Wipe completato, in attesa di approvazione per il cleanup.</summary>
    PendingApproval,

    /// <summary>Cleanup approvato, in esecuzione.</summary>
    CleanupInProgress,

    /// <summary>Cleanup completato con successo su tutti i repository.</summary>
    Completed,

    /// <summary>Cleanup fallito su almeno un repository.</summary>
    Failed,

    /// <summary>Cleanup saltato manualmente dall'operatore.</summary>
    Skipped
}
