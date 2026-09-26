using Microsoft.EntityFrameworkCore;

namespace Hispania.Library.Persistence.EFCore;

/// <summary>Provider-owned translation of database uniqueness errors needed by metadata conflict coordination.</summary>
public interface IUniqueConstraintViolationClassifier
{
    bool IsConfirmedExternalIdentifierConflict(DbUpdateException exception);
}

/// <summary>Fallback used by providers that do not offer a classified conflict. The store rethrows the original exception.</summary>
public sealed class NoUniqueConstraintViolationClassifier : IUniqueConstraintViolationClassifier
{
    public bool IsConfirmedExternalIdentifierConflict(DbUpdateException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return false;
    }
}
