using System.Text.Json;

namespace YTPlaylistManager.Server.Services;

/// <summary>
/// Base de los almacenes en JSON de la carpeta de datos: resuelve la ruta, comparte
/// el lock y las opciones de serialización (una sola instancia, para no anular el
/// caché de metadatos de System.Text.Json creando opciones por llamada).
/// </summary>
public abstract class JsonFileStore
{
    protected static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
    protected static readonly JsonSerializerOptions Compact = new() { WriteIndented = false };

    // Un solo lock para todos los stores: el import de respaldos reemplaza varios
    // archivos como unidad y ningun Mutate en curso debe pisarlo. Nadie hace await
    // bajo lock y Monitor es reentrante, asi que no hay riesgo de deadlock.
    protected static readonly object Sync = new();
    private readonly string _path;

    protected string FilePath => _path;

    protected JsonFileStore(IConfiguration cfg, string fileName)
    {
        _path = Path.Combine(ResolveDataFolder(cfg), fileName);
    }

    /// <summary>Unica fuente de la carpeta de datos (Storage:DataFolder).</summary>
    public static string ResolveDataFolder(IConfiguration cfg)
    {
        var folder = cfg["Storage:DataFolder"] ?? "./data";
        Directory.CreateDirectory(folder);
        return folder;
    }

    /// <summary>Ejecuta bajo el lock global de los stores (import/export de respaldos).</summary>
    public static void RunExclusive(Action fn)
    {
        lock (Sync) fn();
    }

    protected T? Read<T>() where T : class
    {
        if (!File.Exists(_path)) return null;
        return JsonSerializer.Deserialize<T>(File.ReadAllText(_path));
    }

    protected void Write<T>(T value, bool indented = true)
    {
        // Escritura atomica: si el proceso muere a mitad, el archivo original queda intacto.
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, indented ? Indented : Compact));
        File.Move(tmp, _path, overwrite: true);
    }

    protected void DeleteFile()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }
}

/// <summary>Almacén de una lista de registros con el ciclo cargar→mutar→guardar bajo lock.</summary>
public abstract class JsonListStore<T>(IConfiguration cfg, string fileName) : JsonFileStore(cfg, fileName)
    where T : class
{
    public List<T> LoadAll()
    {
        lock (Sync) return Read<List<T>>() ?? [];
    }

    protected void Mutate(Action<List<T>> fn)
    {
        lock (Sync)
        {
            var list = Read<List<T>>() ?? [];
            fn(list);
            Write(list);
        }
    }

    /// <summary>Como <see cref="Mutate"/> pero escribe solo si la función reporta cambios.</summary>
    protected void MutateIf(Func<List<T>, bool> fn)
    {
        lock (Sync)
        {
            var list = Read<List<T>>() ?? [];
            if (fn(list)) Write(list);
        }
    }
}

/// <summary>Contrato de los planes pendientes por cuenta (uniones y reasignaciones).</summary>
public interface IUserScopedPlan
{
    string Id { get; }
    string UserKey { get; set; }
    DateTime CreatedAtUtc { get; }
}

/// <summary>
/// Almacén de planes pendientes keyed por cuenta. PendingUploadStore y
/// PendingSongMoveStore eran copias idénticas salvo el tipo; esta base es la única
/// implementación.
/// </summary>
public abstract class PendingPlanStore<T>(IConfiguration cfg, string fileName) : JsonListStore<T>(cfg, fileName)
    where T : class, IUserScopedPlan
{
    public List<T> LoadForUser(string userKey) =>
        LoadAll().Where(p => p.UserKey == userKey).OrderByDescending(p => p.CreatedAtUtc).ToList();

    public T? Get(string id) => LoadAll().FirstOrDefault(p => p.Id == id);

    public T Add(T plan)
    {
        Mutate(l => l.Add(plan));
        return plan;
    }

    /// <summary>Reemplaza (o agrega) un plan por su Id — usado tras subir parcialmente.</summary>
    public void Replace(T plan) => Mutate(l =>
    {
        l.RemoveAll(p => p.Id == plan.Id);
        l.Add(plan);
    });

    public void Remove(string id) => Mutate(l => l.RemoveAll(p => p.Id == id));

    /// <summary>Reasigna todos los registros a la clave nueva (migración de UserKey).</summary>
    public void MigrateToKey(string newKey) => MutateIf(l =>
    {
        if (!l.Any(p => p.UserKey != newKey)) return false;
        foreach (var p in l) p.UserKey = newKey;
        return true;
    });
}
