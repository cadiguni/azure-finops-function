using System.Net;
using System.Security.Claims;
using System.Collections.Specialized;
using System.Web;
using Azure.Core.Serialization;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Options;

namespace Personal.FinOpsApi.AzureFunctions.UnitTests.Helpers;

internal sealed class TestHttpRequestData : HttpRequestData
{
    private readonly MemoryStream _body = new();
    private readonly Uri _url;
    private readonly string _method;

    public TestHttpRequestData(FunctionContext functionContext, string method, string url)
        : base(functionContext)
    {
        _method = method;
        _url = new Uri(url);
        Headers = new HttpHeadersCollection();
        Cookies = Array.Empty<IHttpCookie>();
        Identities = Array.Empty<ClaimsIdentity>();
    }

    public override Stream Body => _body;

    public override HttpHeadersCollection Headers { get; }
    public override IReadOnlyCollection<IHttpCookie> Cookies { get; }
    public override Uri Url => _url;
    public override NameValueCollection Query => HttpUtility.ParseQueryString(_url.Query);
    public override IEnumerable<ClaimsIdentity> Identities { get; }
    public override string Method => _method;

    public override HttpResponseData CreateResponse()
    {
        return new TestHttpResponseData(FunctionContext);
    }
}

internal sealed class TestHttpResponseData : HttpResponseData
{
    public TestHttpResponseData(FunctionContext functionContext) : base(functionContext)
    {
        Headers = new HttpHeadersCollection();
        Body = new MemoryStream();
        Cookies = new TestHttpCookies();
        StatusCode = HttpStatusCode.OK;
    }

    public override HttpStatusCode StatusCode { get; set; }
    public override HttpHeadersCollection Headers { get; set; }
    public override Stream Body { get; set; }
    public override HttpCookies Cookies { get; }
}

internal sealed class TestHttpCookies : HttpCookies
{
    private readonly List<IHttpCookie> _cookies = new();

    public override void Append(string name, string value)
    {
        _cookies.Add(new TestHttpCookie(name, value));
    }

    public override void Append(IHttpCookie cookie)
    {
        _cookies.Add(cookie);
    }

    public override IHttpCookie CreateNew()
    {
        return new TestHttpCookie(string.Empty, string.Empty);
    }
}

internal sealed class TestHttpCookie : IHttpCookie
{
    public TestHttpCookie(string name, string value)
    {
        Name = name;
        Value = value;
    }

    public string Name { get; }
    public string Value { get; }
    public DateTimeOffset? Expires { get; }
    public bool? HttpOnly { get; }
    public double? MaxAge { get; }
    public string? Domain { get; }
    public string? Path { get; }
    public SameSite SameSite { get; }
    public bool? Secure { get; }
}

internal static class HttpTestHelpers
{
    public static TestHttpRequestData CreateGetRequest(string url)
    {
        var context = new MockFunctionContext();
        return new TestHttpRequestData(context, "GET", url);
    }

    public static async Task<string> ReadBodyAsStringAsync(HttpResponseData response)
    {
        response.Body.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(response.Body);
        return await reader.ReadToEndAsync();
    }
}

internal sealed class MockFunctionContext : FunctionContext
{
    private readonly IDictionary<object, object> _items = new Dictionary<object, object>();
    private readonly IServiceProvider _serviceProvider = new DefaultServiceProvider();
    private readonly IInvocationFeatures _features = new TestInvocationFeatures();

    public override string InvocationId { get; } = Guid.NewGuid().ToString();
    public override string FunctionId { get; } = Guid.NewGuid().ToString();
    public override TraceContext TraceContext { get; } = null!;
    public override BindingContext BindingContext { get; } = null!;
    public override RetryContext RetryContext { get; } = null!;
    public override IServiceProvider InstanceServices { get => _serviceProvider; set { } }
    public override FunctionDefinition FunctionDefinition { get; } = null!;
    public override IDictionary<object, object> Items { get => _items; set { } }
    public override IInvocationFeatures Features => _features;

    private sealed class DefaultServiceProvider : IServiceProvider
    {
        private readonly IOptions<WorkerOptions> _workerOptions = Options.Create(new WorkerOptions
        {
            Serializer = new JsonObjectSerializer()
        });

        public object? GetService(Type serviceType)
        {
            if (serviceType == typeof(IOptions<WorkerOptions>))
            {
                return _workerOptions;
            }

            return null;
        }
    }

    private sealed class TestInvocationFeatures : IInvocationFeatures
    {
        private readonly Dictionary<Type, object> _values = new();

        public TFeature Get<TFeature>()
        {
            return _values.TryGetValue(typeof(TFeature), out var value)
                ? (TFeature)value
                : default!;
        }

        public void Set<TFeature>(TFeature instance)
        {
            if (instance == null)
            {
                _values.Remove(typeof(TFeature));
                return;
            }

            _values[typeof(TFeature)] = instance;
        }

        public IEnumerator<KeyValuePair<Type, object>> GetEnumerator() => _values.GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => _values.GetEnumerator();
    }
}
