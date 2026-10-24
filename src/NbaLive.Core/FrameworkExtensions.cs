#if NETFRAMEWORK
global using System.Net.Http;

namespace NbaLive.Core;

public static class FrameworkExtensions
{
    public static bool Contains(this string text,string value,StringComparison comparison) => text.IndexOf(value,comparison)>=0;
    public static bool StartsWith(this string text,char c) => text.Length>0 && text[0]==c;
    public static IEnumerable<(TFirst First,TSecond Second)> Zip<TFirst,TSecond>(this IEnumerable<TFirst> first,IEnumerable<TSecond> second) => first.Zip(second,(a,b)=>(a,b));
    public static string Replace(this string text,string oldValue,string newValue,StringComparison comparison)
    {
        if(oldValue.Length==0)throw new ArgumentException(nameof(oldValue));
        var result=new System.Text.StringBuilder();var start=0;int index;
        while((index=text.IndexOf(oldValue,start,comparison))>=0){result.Append(text,start,index-start).Append(newValue);start=index+oldValue.Length;}
        return result.Append(text,start,text.Length-start).ToString();
    }
    public static IEnumerable<T> TakeLast<T>(this IEnumerable<T> values,int count)
    {
        if(count<=0)return Enumerable.Empty<T>();var queue=new Queue<T>();foreach(var value in values){queue.Enqueue(value);if(queue.Count>count)queue.Dequeue();}return queue;
    }
    public static IEnumerable<T> DistinctBy<T,TKey>(this IEnumerable<T> values,Func<T,TKey> key,IEqualityComparer<TKey>? comparer=null)
    {
        var keys=new HashSet<TKey>(comparer);foreach(var value in values)if(keys.Add(key(value)))yield return value;
    }
    public static T? MaxBy<T,TKey>(this IEnumerable<T> values,Func<T,TKey> key)
    {
        var found=false;T? best=default;TKey? maximum=default;foreach(var value in values){var next=key(value);if(!found||Comparer<TKey>.Default.Compare(next,maximum!)>0){best=value;maximum=next;found=true;}}return best;
    }
    public static TValue? GetValueOrDefault<TKey,TValue>(this IReadOnlyDictionary<TKey,TValue> values,TKey key) => values.TryGetValue(key,out var result)?result:default;
    public static bool TryAdd<TKey,TValue>(this Dictionary<TKey,TValue> values,TKey key,TValue value)
    {if(values.ContainsKey(key))return false;values.Add(key,value);return true;}
    public static async Task WaitAsync(this Task task,CancellationToken ct)=>await WaitAsync(task,Timeout.InfiniteTimeSpan,ct).ConfigureAwait(false);
    public static async Task WaitAsync(this Task task,TimeSpan timeout,CancellationToken ct=default)
    {
        using var lifetime=CancellationTokenSource.CreateLinkedTokenSource(ct);
        var delay=Task.Delay(timeout,lifetime.Token);var winner=await Task.WhenAny(task,delay).ConfigureAwait(false);
        if(winner==task){lifetime.Cancel();await task.ConfigureAwait(false);return;}
        ct.ThrowIfCancellationRequested();throw new TimeoutException();
    }
    public static async Task<T> WaitAsync<T>(this Task<T> task,TimeSpan timeout,CancellationToken ct=default)
    {await WaitAsync((Task)task,timeout,ct).ConfigureAwait(false);return await task.ConfigureAwait(false);}
    public static async Task<string> ReadAsStringAsync(this HttpContent content,CancellationToken ct)
    {ct.ThrowIfCancellationRequested();var result=await content.ReadAsStringAsync().WaitAsync(TimeSpan.FromSeconds(12),ct).ConfigureAwait(false);return result;}
}
#endif
