using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Catalog.Infrastructure.Persistence;

/// <summary>
/// Traduce el fallo del índice único de 1.2 a una pregunta que la capa de
/// presentación pueda hacer sin saber de SQL Server.
///
/// Vive en .Infrastructure a propósito. "2601 y 2627 son los códigos de
/// violación de unicidad de SQL Server" es conocimiento de la capa de
/// persistencia; si el controller lo comprobara él mismo necesitaría un
/// <c>using Microsoft.Data.SqlClient</c> en Catalog.API, que es meter el motor
/// de base de datos en la capa que habla HTTP. El controller sigue capturando
/// <see cref="DbUpdateException"/> —eso es inevitable si el árbitro de la
/// unicidad es el índice— pero no sabe qué motor hay detrás.
/// </summary>
public static class DbUpdateExceptionExtensions
{
    /// <summary>Violación de índice único (<c>CREATE UNIQUE INDEX</c>).</summary>
    private const int DuplicateKeyRowInUniqueIndex = 2601;

    /// <summary>Violación de constraint UNIQUE o de clave primaria.</summary>
    private const int UniqueConstraintViolation = 2627;

    /// <summary>
    /// Los dos números se comprueban porque SQL Server usa uno u otro según cómo
    /// se declarara la unicidad, y el mismo <c>Sku</c> duplicado saldría con un
    /// código distinto si mañana el índice de 1.2 pasara a ser un constraint.
    /// </summary>
    public static bool IsUniqueConstraintViolation(this DbUpdateException exception)
    {
        return exception.InnerException is SqlException
        {
            Number: DuplicateKeyRowInUniqueIndex or UniqueConstraintViolation,
        };
    }
}
