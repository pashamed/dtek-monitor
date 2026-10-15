using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace DtekBot.Tests;

// SQLite cannot compare/order DateTimeOffset columns in SQL (SQL Server can), so the test database stores them as UTC ticks.
public sealed class UtcTicksModelCustomizer(ModelCustomizerDependencies dependencies) : ModelCustomizer(dependencies)
{
    private static readonly ValueConverter<DateTimeOffset, long> Converter =
        new(v => v.UtcTicks, v => new DateTimeOffset(v, TimeSpan.Zero));

    public override void Customize(ModelBuilder modelBuilder, DbContext context)
    {
        base.Customize(modelBuilder, context);

        foreach (var property in modelBuilder.Model.GetEntityTypes().SelectMany(e => e.GetProperties()))
        {
            if (property.ClrType == typeof(DateTimeOffset) || property.ClrType == typeof(DateTimeOffset?))
            {
                property.SetValueConverter(Converter);
            }
        }
    }
}
