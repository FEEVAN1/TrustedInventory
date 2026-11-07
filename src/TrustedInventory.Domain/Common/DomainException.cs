namespace TrustedInventory.Domain.Common;

/// <summary>A domain rule rejected an operation in the entity's current state (maps to 409 INVALID_STATE).</summary>
public sealed class DomainException(string message) : Exception(message);
