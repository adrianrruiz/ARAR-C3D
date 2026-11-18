using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.DatabaseServices;
using AcException = Autodesk.AutoCAD.Runtime.Exception;

namespace ARAR.Civil3D.Services;

public sealed record SolidSurfaceMoveResult(
    int Lifted,
    int AlreadyAtOrAbove,
    int Skipped,
    IReadOnlyList<string> Messages);

/// <summary>Calcula y aplica desplazamientos verticales de sólidos hasta una superficie TIN.</summary>
public sealed class SolidSurfaceService
{
    private const double ElevationTolerance = 1e-8;

    private sealed record PendingMove(ObjectId SolidId, double DeltaZ, string Handle);

    /// <summary>
    /// Toma como apoyo el punto (centro XY de los límites geométricos, cota Z mínima).
    /// Solo eleva: los sólidos ya apoyados o situados sobre la superficie no se bajan.
    /// </summary>
    public SolidSurfaceMoveResult LiftToSurface(
        Document document,
        IReadOnlyList<ObjectId> solidIds,
        ObjectId surfaceId)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(solidIds);

        if (solidIds.Count == 0)
            return new SolidSurfaceMoveResult(0, 0, 0, Array.Empty<string>());

        using DocumentLock documentLock = document.LockDocument();
        using Transaction transaction = document.Database.TransactionManager.StartTransaction();

        if (transaction.GetObject(surfaceId, OpenMode.ForRead, false) is not TinSurface surface)
            throw new InvalidOperationException("El objeto elegido ya no es una superficie TIN válida.");

        var pendingMoves = new List<PendingMove>(solidIds.Count);
        var messages = new List<string>();
        int alreadyAtOrAbove = 0;
        int skipped = 0;

        // Primero se calculan todas las elevaciones. Así los fallos puntuales de muestreo
        // se informan antes de modificar cualquier sólido.
        foreach (ObjectId solidId in solidIds)
        {
            if (transaction.GetObject(solidId, OpenMode.ForRead, false) is not Solid3d solid)
            {
                skipped++;
                messages.Add($"Objeto {solidId}: ya no está disponible o no es un sólido 3D.");
                continue;
            }

            try
            {
                Extents3d extents = solid.GeometricExtents;
                double centerX = (extents.MinPoint.X + extents.MaxPoint.X) * 0.5;
                double centerY = (extents.MinPoint.Y + extents.MaxPoint.Y) * 0.5;
                double baseElevation = extents.MinPoint.Z;
                double surfaceElevation = surface.FindElevationAtXY(centerX, centerY);

                if (!double.IsFinite(surfaceElevation))
                {
                    skipped++;
                    messages.Add($"Sólido {solid.Handle}: la superficie no devolvió una cota válida en el punto base.");
                    continue;
                }

                double deltaZ = surfaceElevation - baseElevation;
                if (deltaZ <= ElevationTolerance)
                {
                    alreadyAtOrAbove++;
                    continue;
                }

                pendingMoves.Add(new PendingMove(solidId, deltaZ, solid.Handle.ToString()));
            }
            catch (AcException ex)
            {
                skipped++;
                AddinLogger.Error($"No se pudo muestrear la superficie para el sólido {solid.Handle}.", ex);
                messages.Add($"Sólido {solid.Handle}: no se pudo obtener la cota de la superficie en su punto base.");
            }
            catch (System.ArgumentException ex)
            {
                skipped++;
                AddinLogger.Error($"Punto base fuera de la superficie para el sólido {solid.Handle}.", ex);
                messages.Add($"Sólido {solid.Handle}: el punto base está fuera de la superficie.");
            }
        }

        foreach (PendingMove move in pendingMoves)
        {
            var solid = (Solid3d)transaction.GetObject(move.SolidId, OpenMode.ForWrite, false);
            solid.TransformBy(Matrix3d.Displacement(new Vector3d(0.0, 0.0, move.DeltaZ)));
        }

        transaction.Commit();
        return new SolidSurfaceMoveResult(pendingMoves.Count, alreadyAtOrAbove, skipped, messages);
    }
}
