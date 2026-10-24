using System.Security.Cryptography;

namespace NbaLive.Core;

public static class RuntimeCompat
{
    public static bool IsAsciiDigit(char c) => c>='0' && c<='9';
    public static bool IsAsciiLetter(char c) => c>='a' && c<='z' || c>='A' && c<='Z';
    public static bool IsAsciiLetterOrDigit(char c) => IsAsciiDigit(c) || IsAsciiLetter(c);
    public static TimeSpan Elapsed(long start) => TimeSpan.FromSeconds((System.Diagnostics.Stopwatch.GetTimestamp()-start)/(double)System.Diagnostics.Stopwatch.Frequency);
    public static System.Net.HttpStatusCode? StatusCode(System.Net.Http.HttpRequestException error) =>
        error is SourceHttpException source ? source.Code :
#if NETFRAMEWORK
        null;
#else
        error.StatusCode;
#endif
    public static int Clamp(int value,int min,int max) => min<=max?Math.Min(max,Math.Max(min,value)):throw new ArgumentException("Invalid bounds.");
    public static double Clamp(double value,double min,double max) => min>max?throw new ArgumentException("Invalid bounds."):Math.Min(max,Math.Max(min,value));
    public static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    public static bool IsDefined<T>(T value) where T:struct => Enum.IsDefined(typeof(T),value);
    public static string Sha256Hex(byte[] bytes)
    {
        using var sha=SHA256.Create();return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-","");
    }
    public static void ReplaceFile(string source,string destination)
    {
#if NETFRAMEWORK
        if(File.Exists(destination)) File.Replace(source,destination,null);
        else File.Move(source,destination);
#else
        File.Move(source,destination,true);
#endif
    }
}

internal sealed class SourceHttpException : System.Net.Http.HttpRequestException
{
    internal System.Net.HttpStatusCode Code { get; }
    internal SourceHttpException(System.Net.HttpStatusCode code)
#if NETFRAMEWORK
        :base("HTTP "+(int)code)
#else
        :base("HTTP "+(int)code,null,code)
#endif
    {Code=code;}
}
