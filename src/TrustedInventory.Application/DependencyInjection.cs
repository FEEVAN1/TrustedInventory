using FluentValidation;
using Microsoft.Extensions.DependencyInjection;

namespace TrustedInventory.Application;

public static class DependencyInjection
{
    /// <summary>
    /// Registers every application service (concrete classes named *Service) and every FluentValidation validator
    /// in this assembly. Endpoints inject services directly; there is no mediator or request dispatch (DEC-015).
    /// </summary>
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        var types = typeof(DependencyInjection).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false, IsGenericTypeDefinition: false })
            .ToList();

        foreach (var service in types.Where(t => t.Name.EndsWith("Service", StringComparison.Ordinal) && t.IsPublic))
            services.AddScoped(service);

        foreach (var validator in types)
            foreach (var contract in validator.GetInterfaces()
                         .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IValidator<>)))
                services.AddSingleton(contract, validator);

        return services;
    }
}
