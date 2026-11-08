using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PartitionManager.Core.Execution;
using PartitionManager.Core.Models;

namespace PartitionManager.PrivilegedHelper.Engine;

public sealed record JournalEvent(int Sequence, Guid OperationId, DateTimeOffset Utc, string Phase,
    string Detail, WriteOperationRequest? Request, DiskInfo? Layout, DiskInfo? IntendedLayout,
    string Recovery, string PreviousHash);
public sealed record JournalLine(JournalEvent Event, string Sha256);
public sealed record JournalInspection(string File, bool Valid, bool RequiresReview, IReadOnlyList<JournalEvent> Events, string Problem);

public interface IOperationJournal
{
    void Begin(Guid id, WriteOperationRequest request, DiskInfo initial, DiskInfo intended);
    void Record(string phase, string detail, DiskInfo? layout = null);
}

/// <summary>Append-only, flushed write-ahead journal. Never replays storage commands.</summary>
public sealed class FileOperationJournal : IOperationJournal, IDisposable
{
    public const string RecoveryInstructions = "Do not retry automatically. Re-enumerate the identified disk, compare initial/intended/actual layouts, verify protected partitions and file hashes. Retain this journal for manual review; no atomic rollback is available.";
    private readonly string _directory;
    private readonly Action<string>? _validate;
    private readonly FileStream _lease;
    private string? _file;
    private Guid _id;
    private int _sequence;
    private string _hash = "";

    // Production supplies an ACL validator; tests use isolated temporary directories.
    public FileOperationJournal(string directory, Action<string>? validate = null)
    {
        _directory = Path.GetFullPath(directory);
        _validate = validate;
        if (!Directory.Exists(_directory)) throw new DirectoryNotFoundException(_directory);
        _validate?.Invoke(_directory);
        RejectReparse(_directory);
        var leasePath = Path.Combine(_directory,"journal.lock");
        if (File.Exists(leasePath)) RejectReparse(leasePath);
        _lease = new FileStream(leasePath,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
    }

    public void Begin(Guid id, WriteOperationRequest request, DiskInfo initial, DiskInfo intended)
    {
        _validate?.Invoke(_directory);
        // Conservatively block all further real work after an uncertain prior run,
        // including when the disk has been renumbered or disconnected.
        var history = Inspect(_directory);
        if (history.Any(x => x.RequiresReview))
            throw new IOException("RECOVERY_REVIEW_REQUIRED: an interrupted, failed or corrupt journal requires manual investigation. " + RecoveryInstructions);
        if (request.Kind == WriteOperationKind.InitializeDisk && history.Any(x =>
            x.Events.FirstOrDefault()?.Request is { Kind: WriteOperationKind.InitializeDisk } old &&
            string.Equals(old.Disk.VirtualImagePath, request.Disk.VirtualImagePath, StringComparison.OrdinalIgnoreCase)))
            throw new IOException("INITIALIZATION_APPROVAL_CONSUMED: this image already has a recorded initialization. Fresh provisioning and independent review are required.");
        _id=id; _sequence=0; _hash="";
        _file=Path.Combine(_directory,$"operation-{id:N}.jsonl");
        using (var created=new FileStream(_file,FileMode.CreateNew,FileAccess.Write,FileShare.Read,4096,FileOptions.WriteThrough)) created.Flush(true);
        Append("Prepared","Identity validated; operation not yet dispatched",request,initial,intended);
    }

    public void Record(string phase,string detail,DiskInfo? layout=null) => Append(phase,detail,null,layout,null);

    private void Append(string phase,string detail,WriteOperationRequest? request,DiskInfo? layout,DiskInfo? intended)
    {
        if (_file is null) throw new InvalidOperationException("No operation was begun.");
        _validate?.Invoke(_directory);
        RejectReparse(_file);
        var entry=new JournalEvent(_sequence,_id,DateTimeOffset.UtcNow,phase,detail,request,layout,intended,RecoveryInstructions,_hash);
        var hash=Hash(entry);
        var bytes=Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new JournalLine(entry,hash))+"\n");
        using(var file=new FileStream(_file,FileMode.Append,FileAccess.Write,FileShare.Read,4096,FileOptions.WriteThrough))
        { file.Write(bytes); file.Flush(true); }
        _sequence++; _hash=hash;
    }

    public static IReadOnlyList<JournalInspection> Inspect(string directory)
    {
        var result=new List<JournalInspection>();
        foreach(var path in Directory.EnumerateFiles(directory,"operation-*.jsonl").Order())
        {
            var events=new List<JournalEvent>(); var previous=""; Guid? id=null; string problem="";
            try
            {
                RejectReparse(path);
                if(new FileInfo(path).Length>16L*1024*1024) throw new IOException("Journal exceeds the 16 MiB inspection limit.");
                foreach(var text in File.ReadLines(path))
                {
                    var line=JsonSerializer.Deserialize<JournalLine>(text) ?? throw new IOException("Empty record");
                    var e=line.Event;
                    if(e is null || e.Sequence!=events.Count || e.PreviousHash!=previous || Hash(e)!=line.Sha256 ||
                        (id is not null && e.OperationId!=id) || Path.GetFileName(path)!=$"operation-{e.OperationId:N}.jsonl")
                        throw new IOException("Sequence, identity or hash-chain mismatch");
                    if(events.Count==0 && (e.Phase!="Prepared" || e.Request is null || e.Layout is null || e.IntendedLayout is null))
                        throw new IOException("Missing initial intent");
                    if(events.LastOrDefault()?.Phase is "Verified" or "Failed" or "NotStarted") throw new IOException("Data after terminal event");
                    id=e.OperationId; previous=line.Sha256; events.Add(e);
                }
                if(events.Count==0) throw new IOException("Empty journal (interrupted before intent was durable)");
                using var file=File.OpenRead(path); file.Seek(-1,SeekOrigin.End);
                if(file.ReadByte()!='\n') throw new IOException("Torn final record");
            }
            catch(Exception ex) when(ex is IOException or JsonException or UnauthorizedAccessException or ArgumentException) { problem=ex.Message; }
            // "NotStarted" is written only when the relocation's own progress proves no write was issued (e.g. volume in use).
            result.Add(new(path,problem.Length==0,problem.Length>0 || events.LastOrDefault()?.Phase is not ("Verified" or "NotStarted"),events,problem));
        }
        return result;
    }

    private static string Hash(JournalEvent e)=>Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(e)));
    private static void RejectReparse(string path)
    {
        FileSystemInfo? item=Directory.Exists(path)?new DirectoryInfo(path):new FileInfo(path);
        while(item is not null)
        {
            if((item.Attributes&FileAttributes.ReparsePoint)!=0) throw new IOException("Journal path contains a reparse point.");
            item=item is DirectoryInfo dir?dir.Parent:((FileInfo)item).Directory;
        }
    }
    public void Dispose()=>_lease.Dispose();
}

/// <summary>
/// Opens the exclusive journal only when the first write begins, so a helper that only serves read-only requests
/// (Data Recovery) never holds the journal lease and never blocks a second helper doing partition work. Once opened,
/// the lease is held until the helper exits (one writer at a time across sessions).
/// </summary>
public sealed class LazyOperationJournal(Func<FileOperationJournal> open) : IOperationJournal, IDisposable
{
    private FileOperationJournal? _journal;

    public void Begin(Guid id, WriteOperationRequest request, DiskInfo initial, DiskInfo intended)
    {
        if (_journal is null)
        {
            try { _journal = open(); }
            catch (IOException ex) when ((ex.HResult & 0xFFFF) is 32 or 33)
            {
                throw new IOException("Another privileged helper is still running an operation; wait for it to finish, then retry. Nothing was changed.", ex);
            }
        }
        _journal.Begin(id, request, initial, intended);
    }

    public void Record(string phase, string detail, DiskInfo? layout = null) =>
        (_journal ?? throw new InvalidOperationException("No operation was begun.")).Record(phase, detail, layout);

    public void Dispose() => _journal?.Dispose();
}

/// <summary>Raised after a mutating call returned, when its outcome could not be observed: never "nothing changed".</summary>
internal sealed class PostMutationException(string message) : IOException(message);

/// <summary>Flush intent before every mutating backend call, then record its observed outcome.</summary>
internal sealed class JournaledBackend(IStorageWriteBackend inner,IOperationJournal journal,Func<DiskInfo?> observe,DiskInfo initial) : IStorageWriteBackend
{
    private DiskInfo _expected=initial;
    private T Write<T>(string name,Func<T> call)
    {
        var current=observe();
        if(current is null || current.IsVirtual!=_expected.IsVirtual || current.VirtualImagePath!=_expected.VirtualImagePath ||
            !DiskIdentityMatcher.Match(DiskIdentity.From(_expected),[current]).IsMatch)
            throw new IOException("Disk identity/layout changed before a mutation checkpoint; execution stopped.");
        journal.Record("StepStarted",name,current);
        T result;
        try { result=call(); }
        catch(Exception ex) { journal.Record("StepUncertain",name+": "+ex.Message,observe()); throw; }
        var after=observe();
        journal.Record("StepReturned",name,after);
        if(after is null) throw new PostMutationException("Disk disconnected after mutation; inspect journal before recovery.");
        _expected=after;
        return result;
    }
    private void Write(string name,Action call)=>Write(name,()=>{call();return 0;});
    public IDisposable? PinBackingImage(DiskInfo d)=>inner.PinBackingImage(d);
    public void SetVolumeLabel(int d,int p,string l)=>Write("Set label",()=>inner.SetVolumeLabel(d,p,l));
    public void AddDriveLetter(int d,int p,char l)=>Write("Add letter",()=>inner.AddDriveLetter(d,p,l));
    public void RemoveDriveLetter(int d,int p,char l)=>Write("Remove letter",()=>inner.RemoveDriveLetter(d,p,l));
    public (long Min,long Max) GetSupportedSize(int d,int p)=>inner.GetSupportedSize(d,p);
    public void Resize(int d,int p,long size)=>Write("Resize",()=>inner.Resize(d,p,size));
    public int CreatePartition(int d,long offset,long size)=>Write("Create",()=>inner.CreatePartition(d,offset,size));
    public void Format(int d,int p,string fs,string label,int cluster,bool full)=>Write("Format",()=>inner.Format(d,p,fs,label,cluster,full));
    public void DeletePartition(int d,int p)=>Write("Delete",()=>inner.DeletePartition(d,p));
    public void InitializeDisk(int d,PartitionStyle style)=>Write("Initialize",()=>inner.InitializeDisk(d,style));
    public void SetPartitionReadOnly(int d,int p,bool readOnly)=>Write("Set read-only",()=>inner.SetPartitionReadOnly(d,p,readOnly));
    // One durable record per verified chunk: an overlapping move can only be resumed exactly after the last completed chunk.
    /// <summary>
    /// One compact checkpoint per chunk for the first 4096 chunks, then every 64th, so a large move cannot outgrow the
    /// 16 MiB inspection limit. A checkpoint that cannot be written never aborts a copy in progress (stopping an
    /// overlapping copy half-way is the worst outcome); the miss is recorded at the end.
    /// </summary>
    public RelocationResult RelocatePartition(int d,PartitionTarget p,long offset,Action<long,long>? checkpoint,bool physicalTestDisk=false)
    {
        journal.Record("RelocationPlanned",$"partition {p.PartitionNumber}: {p.SizeBytes} bytes from {p.OffsetBytes} to {offset}");
        long n=0, missed=0;
        var result=Write("Relocate (locked block copy)",()=>inner.RelocatePartition(d,p,offset,(at,copied)=>
        {
            n++;
            if(n<=4096 || n%64==0)
            {
                try { journal.Record("ChunkCopied",$"+{at} {copied}"); }
                catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) { missed++; }
            }
            checkpoint?.Invoke(at,copied);
        },physicalTestDisk));
        if(missed>0) journal.Record("CheckpointsMissed",$"{missed} checkpoint record(s) could not be written");
        return result;
    }
}
