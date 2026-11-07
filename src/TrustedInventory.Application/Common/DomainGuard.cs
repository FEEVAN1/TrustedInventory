using TrustedInventory.Application.Errors;
using TrustedInventory.Domain.Common;

namespace TrustedInventory.Application.Common;

/// <summary>Runs a domain operation and reports a broken domain rule as 409 INVALID_STATE.</summary>
public static class DomainGuard
{
    public static void Run(Action change)
    {
        try
        {
            change();
        }
        catch (DomainException ex)
        {
            throw AppException.InvalidState(ex.Message);
        }
    }

    public static T Run<T>(Func<T> create)
    {
        try
        {
            return create();
        }
        catch (DomainException ex)
        {
            throw AppException.InvalidState(ex.Message);
        }
    }
}
