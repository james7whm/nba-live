internal sealed class TaskCompletionSource : System.Threading.Tasks.TaskCompletionSource<bool>
{
    public TaskCompletionSource():base(){}
    public TaskCompletionSource(TaskCreationOptions options):base(options){}
    public bool TrySetResult()=>base.TrySetResult(true);
    public void SetResult()=>base.SetResult(true);
}
