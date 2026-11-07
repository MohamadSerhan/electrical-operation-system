using System.Diagnostics;
using System.Drawing;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;
using EOEMS.PowerTransformersScadaUI.V5;

internal static class ZetaCloudValidation
{
    private static readonly UserSession Admin = new() { UserId = "cloud-fixture", Username = "cloud-fixture", RoleName = "Administrator" };
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;
    private const string Token = "fixture-only-not-a-real-credential";
    private const string EventJson = """{"events":[{"id":"vendor-1","timestamp":"2026-11-04T10:00:00+02:00","event_type":"Fire","message":"fixture historical event","zone":2,"device_address":"L1-A2","description":"fixture detector"}]}""";
    private const string ObservedEvents = """[{"id":991214,"hwaddr":"78392d300758","networkId":0,"category":"fault","title":"Fixture fault with 2, Zone Monitor Module","date":"2026-11-03T15:20:00.000Z","location":"Fixture area (Address 2)"},{"id":987288,"hwaddr":"78392d300758","networkId":0,"category":"service","title":"Fixture Reset button pressed","date":"2026-10-30T15:26:00.000Z","location":"No location"}]""";
    private static int assertions;
    private static WindowsFormsSynchronizationContext? uiContext;

    internal static void Run(string evidence)
    {
        Directory.CreateDirectory(evidence);
        var temporary = Path.Combine(Path.GetTempPath(), "eoems-cloud-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            Client().GetAwaiter().GetResult();
            Authorization().GetAwaiter().GetResult();
            ManualLogin(temporary).GetAwaiter().GetResult();
            SavedLoginRecovery(temporary).GetAwaiter().GetResult();
            ObservedContract().GetAwaiter().GetResult();
            Persistence(temporary).GetAwaiter().GetResult();
            Service(temporary).GetAwaiter().GetResult();
            AuthenticationFailures(temporary).GetAwaiter().GetResult();
            Ui(temporary, evidence);
            File.WriteAllText(Path.Combine(evidence, "cloud-validation.json"), JsonSerializer.Serialize(new
            {
                assertions,
                timestampUtc = DateTime.UtcNow,
                realApiContacted = false,
                schema = "Operator-observed GET start/end UTC date query and date/category/title/location fields. Live application authorization remains UNVERIFIED."
            }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"Zeta cloud assertions passed: {assertions}. No real API contacted.");
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(temporary, true);
        }
    }

    private static FireConnectionSettings Settings()
    {
        var settings = new FireConnectionSettings();
        settings.SelectProfile(FireConnectionType.ZetaCloud);
        settings.CloudApiEndpoint = ZetaCloudEventClient.EventRoute;
        settings.CloudSiteIdentifier = "78392d300758";
        settings.CloudSiteName = "Fixture only";
        settings.CloudAuthentication = "API token";
        return settings;
    }

    private static async Task SavedLoginRecovery(string root)
    {
        const string email = "reconnect-fixture@example.invalid";
        const string password = "synthetic-auto-reconnect-only";
        var settings = Settings();
        settings.AutoConnect = true;
        settings.AutoReconnect = true;
        var store = new FireConnectionStore(Path.Combine(root, "saved-login", "fire.json"));
        store.Save(settings, "", "", false, Admin);
        using (var handler = new Handler())
        using (var service = new FireConnectionService(store, cloudClient: new ZetaCloudEventClient(handler)))
        {
            var result = await service.SignInCloudAsync(settings, email, password, Admin);
            Check(result.Success && service.ReadStatus().Monitoring && service.ReadStatus().HasSavedCloudLogin,
                "successful login enables continuous monitoring when AutoConnect is enabled");
            await service.StopAsync();
        }
        var saved = store.Load();
        Check(saved.ProtectedCloudEmail.Length > 0 && saved.ProtectedCloudPassword.Length > 0 &&
            !File.ReadAllText(store.FilePath).Contains(email) && !File.ReadAllText(store.FilePath).Contains(password),
            "dedicated Zeta email and password survive restart only as DPAPI ciphertext");
        var settingsOnly = settings.Copy(); settingsOnly.CloudSiteName = "Renamed without re-entering secrets";
        store.Save(settingsOnly, "", "", false, Admin);
        Check(store.Load().ProtectedCloudEmail == saved.ProtectedCloudEmail && store.Load().ProtectedCloudPassword == saved.ProtectedCloudPassword,
            "ordinary settings saves preserve the protected Zeta login");
        using (var handler = new Handler { RejectAuthRequests = 1 })
        using (var reopened = new FireConnectionService(new FireConnectionStore(store.FilePath), cloudClient: new ZetaCloudEventClient(handler)))
        {
            await reopened.StartAsync();
            await Until(() => reopened.ReadStatus().State == "ONLINE");
            Check(handler.LoginRequests == 1 && handler.AuthRequests == 2 && handler.EventRequests == 1 &&
                handler.MaximumActive == 1 && reopened.ReadStatus().Monitoring,
                "startup renews an expired saved account token once and resumes monitoring without UI/password entry");
            using var submitted = JsonDocument.Parse(handler.LastLoginBody);
            Check(submitted.RootElement.GetProperty("email").GetString() == email &&
                submitted.RootElement.GetProperty("password").GetString() == password, "restart uses exactly the encrypted saved login");
            Check(!reopened.DiagnosticExport().Contains(email) && !reopened.DiagnosticExport().Contains(password) &&
                !File.ReadAllText(store.FilePath + ".events.jsonl").Contains(password), "automatic login never leaks credentials in diagnostics or logs");
            await reopened.StopAsync();
        }
        using (var handler = new Handler { RejectEventRequests = 1 })
        using (var service = new FireConnectionService(store, cloudClient: new ZetaCloudEventClient(handler)))
        {
            var result = await service.TestCloudAsync(settings, false, true, Admin);
            Check(result.Success && handler.LoginRequests == 1 && handler.AuthRequests == 2 && handler.EventRequests == 2,
                "expired event session recovers once through saved login and fresh claims");
        }
        using (var handler = new Handler { RejectAuthRequests = 1, RejectedAuthStatus = HttpStatusCode.Forbidden, Challenge = "error=\"expired_token\"" })
        using (var service = new FireConnectionService(store, cloudClient: new ZetaCloudEventClient(handler)))
        {
            var result = await service.TestCloudAsync(settings, false, true, Admin);
            Check(result.Success && handler.LoginRequests == 1, "explicit expired-token403 can recover without misclassifying permission-denied403");
        }
        using (var handler = new Handler { RejectEventRequests = 1, RejectedEventStatus = HttpStatusCode.ServiceUnavailable })
        using (var service = new FireConnectionService(store, cloudClient: new ZetaCloudEventClient(handler)))
        {
            await service.StartAsync();
            await Until(() => service.ReadStatus().ConsecutiveFailures > 0);
            Check(service.ReadStatus().Monitoring && !service.ReadStatus().CloudCredentialRenewalRequired &&
                handler.LoginRequests == 0, "temporary server outage keeps automatic recovery running without asking for credentials");
            await Until(() => service.ReadStatus().State == "ONLINE", 35000);
            Check(handler.EventRequests == 2 && handler.LoginRequests == 0 && service.ReadStatus().ConsecutiveFailures == 0,
                "actual background polling automatically recovers after temporary server failure and respects retry cadence");
            await service.StopAsync();
        }
        using (var handler = new Handler { AuthStatus = HttpStatusCode.Unauthorized, LoginStatus = HttpStatusCode.Unauthorized })
        using (var service = new FireConnectionService(store, cloudClient: new ZetaCloudEventClient(handler)))
        {
            await service.StartAsync();
            await Until(() => !service.ReadStatus().Monitoring);
            Check(handler.LoginRequests == 1 && handler.AuthRequests == 1 && service.ReadStatus().CloudCredentialRenewalRequired &&
                store.Load().ProtectedCloudPassword == saved.ProtectedCloudPassword,
                "rejected saved password stops after one attempt, preserves credentials and explicitly requests correction");
        }
        using (var handler = new Handler { AuthStatus = HttpStatusCode.Forbidden, AuthBody = """{"err":"insufficient_scope"}""" })
        using (var service = new FireConnectionService(store, cloudClient: new ZetaCloudEventClient(handler)))
        {
            await service.StartAsync();
            await Until(() => !service.ReadStatus().Monitoring);
            Check(handler.LoginRequests == 0 && service.ReadStatus().CloudCredentialRenewalRequired,
                "permission denial never triggers repeated password logins");
        }
        using (var handler = new Handler { AuthStatus = HttpStatusCode.Unauthorized, LoginStatus = HttpStatusCode.ServiceUnavailable })
        using (var service = new FireConnectionService(store, cloudClient: new ZetaCloudEventClient(handler)))
        {
            await service.StartAsync();
            await Until(() => service.ReadStatus().ConsecutiveFailures > 0);
            Check(service.ReadStatus().Monitoring && !service.ReadStatus().CloudCredentialRenewalRequired &&
                handler.LoginRequests == 1 && store.Load().ProtectedCloudPassword == saved.ProtectedCloudPassword,
                "temporary login-service failure keeps bounded recovery active and never discards the saved password");
            await service.StopAsync();
        }
        using (var handler = new Handler { AuthStatus = HttpStatusCode.Unauthorized, LoginStatus = HttpStatusCode.TooManyRequests, RetryAfter = 120 })
        using (var service = new FireConnectionService(store, cloudClient: new ZetaCloudEventClient(handler)))
        {
            var result = await service.TestCloudAsync(settings, false, true, Admin);
            var count = handler.Requests;
            await Throws<InvalidOperationException>(() => service.TestCloudAsync(settings, false, true, Admin),
                "automatic renewal honors shared login rate-limit cooldown");
            Check(result.HttpStatus == 429 && handler.LoginRequests == 1 && count == handler.Requests,
                "automatic login cannot create a rate-limit retry storm");
        }
        settings.AutoReconnect = false;
        store.Save(settings, "", "", false, Admin);
        using (var handler = new Handler { AuthStatus = HttpStatusCode.Unauthorized })
        using (var service = new FireConnectionService(store, cloudClient: new ZetaCloudEventClient(handler)))
        {
            await service.StartAsync();
            await Until(() => !service.ReadStatus().Monitoring);
            Check(handler.LoginRequests == 0, "explicitly disabled Recovery prevents automatic password login");
        }
        settings.AutoReconnect = true;
        var noToken = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(store.FilePath))!;
        noToken["ProtectedCloudToken"] = "";
        File.WriteAllText(store.FilePath, noToken.ToJsonString());
        using (var handler = new Handler())
        using (var service = new FireConnectionService(store, cloudClient: new ZetaCloudEventClient(handler)))
        {
            var result = await service.TestCloudAsync(settings, false, true, Admin);
            Check(result.Success && handler.LoginRequests == 1 && service.ReadStatus().HasCloudToken,
                "saved login recovers a missing session token and updates credential-presence status");
        }
        store.Save(settings, "", "", false, Admin, cloudToken: Token);
        Check(store.Load().ProtectedCloudEmail == "" && store.Load().ProtectedCloudPassword == "",
            "explicit token replacement cannot silently recover into the previous saved account");
        store.Save(settings, "", "", false, Admin, cloudToken: Token, cloudEmail: email, cloudPassword: password);
        store.Save(settings, "", "", false, Admin, clearCloudToken: true);
        Check(store.Load().ProtectedCloudToken == "" && store.Load().ProtectedCloudEmail == "" && store.Load().ProtectedCloudPassword == "",
            "Clear Token removes saved login too, so no automatic recreation occurs");
    }

    private static async Task ManualLogin(string root)
    {
        const string email = "fixture-user@example.invalid";
        const string password = "fixture-only-password-! \" \\";
        var settings = Settings();
        var store = new FireConnectionStore(Path.Combine(root, "manual-login", "fire.json"));
        store.Save(settings, "", "", false, Admin);
        using var handler = new Handler { Body = ObservedEvents };
        using var service = new FireConnectionService(store, cloudClient: new ZetaCloudEventClient(handler));
        var result = await service.SignInCloudAsync(settings, email, password, Admin);
        Check(result.Success && result.Authenticated && handler.LoginRequests == 1 && handler.AuthRequests == 1 &&
            handler.EventRequests == 1, "manual login obtains session, claims and parsed/persisted event response in order");
        Check(handler.LastLoginAuthorization == "" && handler.LastLoginMethod == HttpMethod.Post &&
            handler.LastLoginUri == "https://www.zetaremote.com/api//auth/login", "documented login POST uses approved double slash and no saved Bearer header");
        using (var json = JsonDocument.Parse(handler.LastLoginBody))
            Check(json.RootElement.GetProperty("email").GetString() == email && json.RootElement.GetProperty("password").GetString() == password &&
                json.RootElement.EnumerateObject().Count() == 2, "login JSON contains only user-entered email/password without altering password");
        Check(handler.LastAuthAuthorization == "Bearer " + Handler.LoginToken &&
            handler.LastAuthorization == "Bearer " + Handler.PanelToken, "session and claims credentials are never interchanged");
        Check(service.CloudEventCount(settings.CloudSiteIdentifier) == 2 && service.ReadStatus().State == "ONLINE" &&
            !service.ReadStatus().Monitoring, "manual sign-in syncs historical events only; continuous polling requires explicit Connect");
        Check(store.Load().ProtectedCloudToken.Length > 0 && !File.ReadAllText(store.FilePath).Contains(Handler.LoginToken) &&
            !File.ReadAllText(store.FilePath).Contains(password) && !File.ReadAllText(store.FilePath).Contains(email),
            "DPAPI token and login persist without plaintext email/password in connection settings");
        handler.Delay = 50;
        await service.ConnectAsync(Admin);
        await Until(() => handler.Active > 0);
        result = await service.SignInCloudAsync(settings, email, password, Admin);
        Check(result.Success && !service.ReadStatus().Monitoring && handler.MaximumActive == 1,
            "manual sign-in cancels old monitoring before login and never overlaps network requests");
        handler.Delay = 0;
        foreach (var actor in new[] { new UserSession { UserId = "reader", Username = "reader", RoleName = "Operator" },
            new UserSession { RoleName = "Administrator" },
            new UserSession { UserId = "preview", Username = "preview", RoleName = "Administrator", IsOfflinePreview = true } })
            await Throws<UnauthorizedAccessException>(() => service.SignInCloudAsync(settings, email, password, actor),
                "login requires authenticated administrator, never offline preview");
        var previous = store.Load().ProtectedCloudToken;
        var events = handler.EventRequests;
        var exchanges = handler.AuthRequests;
        foreach (var code in new[] { 401, 403, 500, 302 })
        {
            handler.LoginStatus = (HttpStatusCode)code;
            handler.LoginBody = """{"err":"invalid_token","message":"fixture confidential password echo"}""";
            var before = handler.LoginRequests;
            result = await service.SignInCloudAsync(settings, email, password, Admin);
            Check(!result.Success && result.Stage == "LOGIN" && result.HttpStatus == code && result.ErrorOrigin == "ZETA HTTP" &&
                handler.LoginRequests == before + 1 && handler.EventRequests == events && handler.AuthRequests == exchanges,
                "failed login neither retries nor falls through to claims/events");
            Check(store.Load().ProtectedCloudToken == previous && service.CloudEventCount(settings.CloudSiteIdentifier) == 2 &&
                service.ReadStatus().TransportHealth == "DISCONNECTED", "failed login preserves saved token/history and leaves monitoring stopped");
            Check(!result.ErrorMessage.Contains(password) && !result.ErrorMessage.Contains("confidential"),
                "login errors never echo request credentials or arbitrary server errors");
        }

        handler.LoginStatus = HttpStatusCode.OK;
        foreach (var body in new[] { "{}", "{", """{"token":{}}""", """{"token":"Bearer"}""", new string('x', 65537) })
        {
            handler.LoginBody = body;
            result = await service.SignInCloudAsync(settings, email, password, Admin);
            Check(!result.Success && result.ErrorOrigin == "RESPONSE VALIDATION" &&
                store.Load().ProtectedCloudToken == previous && handler.EventRequests == events && handler.AuthRequests == exchanges,
                "malformed login response cannot replace saved token or claim synchronization");
        }
        handler.LoginBody = null;
        var count = handler.LoginRequests;
        result = await service.SignInCloudAsync(settings, email, "", Admin);
        Check(!result.Success && result.HttpStatus is null && handler.LoginRequests == count, "missing password is rejected without HTTP login");
        handler.Delay = 2000;
        var shortTimeout = settings.Copy();
        shortTimeout.TimeoutMilliseconds = 1000;
        result = await service.SignInCloudAsync(shortTimeout, email, password, Admin);
        Check(!result.Success && result.Stage == "LOGIN TIMEOUT" && store.Load().ProtectedCloudToken == previous,
            "login timeout preserves prior token without retry");
        using (var cancel = new CancellationTokenSource(30))
            await Throws<OperationCanceledException>(() => service.SignInCloudAsync(settings, email, password, Admin, cancel.Token),
                "login cancellation promptly propagates and releases request gates");
        handler.Delay = 0;
        handler.AuthStatus = HttpStatusCode.Unauthorized;
        result = await service.SignInCloudAsync(settings, email, password, Admin);
        Check(!result.Success && result.Stage == "CLAIMS AUTHORIZATION" && service.ReadStatus().State != "ONLINE",
            "accepted login alone cannot claim site/event authorization");
        handler.AuthStatus = HttpStatusCode.OK;
        handler.Status = HttpStatusCode.Unauthorized;
        result = await service.SignInCloudAsync(settings, email, password, Admin);
        Check(!result.Success && result.Stage == "EVENT REQUEST" && !service.ReadStatus().Monitoring,
            "claims success alone cannot claim event access");
        var export = service.DiagnosticExport();
        Check(!export.Contains(email) && !export.Contains(password) && !export.Contains(Handler.LoginToken) &&
            !export.Contains(Handler.PanelToken), "login/claims/event diagnostics contain no credentials");
        handler.Status = HttpStatusCode.OK;
        handler.LoginStatus = HttpStatusCode.TooManyRequests;
        handler.RetryAfter = 120;
        result = await service.SignInCloudAsync(settings, email, password, Admin);
        count = handler.Requests;
        await Throws<InvalidOperationException>(() => service.SignInCloudAsync(settings, email, password, Admin),
            "login obeys shared429 cooldown without automatic retries");
        Check(result.RetryAfterSeconds == 120 && handler.Requests == count, "login rate limit blocks new network calls");
        var storagePath = Path.Combine(root, "login-storage-error", "fire.json");
        var storageStore = new FireConnectionStore(storagePath);
        storageStore.Save(settings, "", "", false, Admin);
        using var storageHandler = new Handler();
        using var storageService = new FireConnectionService(storageStore, cloudClient: new ZetaCloudEventClient(storageHandler));
        File.Delete(storagePath);
        Directory.CreateDirectory(storagePath);
        result = await storageService.SignInCloudAsync(settings, email, password, Admin);
        Check(!result.Success && result.Stage == "LOGIN TOKEN STORAGE" && result.ErrorOrigin == "LOCAL STORAGE" &&
            storageHandler.AuthRequests == 0 && storageHandler.EventRequests == 0,
            "protected-token storage failure cannot continue to claims or event requests");
        using var disabled = new FireConnectionService(new FireConnectionStore(Path.Combine(root, "login-disabled", "fire.json")),
            allowCommunication: false, cloudClient: new ZetaCloudEventClient(new Handler()));
        await Throws<InvalidOperationException>(() => disabled.SignInCloudAsync(settings, email, password, Admin),
            "disabled communication forbids even an explicitly invoked login request");
    }

    private static void LoginDialog(string evidence)
    {
        var type = typeof(FireConnectionSettingsView).Assembly.GetType("EOEMS.PowerTransformersScadaUI.V5.ZetaSignInDialog")!;
        var attempts = 0;
        var received = false;
        var pending = new TaskCompletionSource<string>();
        CancellationToken requestCancellation = default;
        Func<string, string, CancellationToken, Task<string>> work = (email, password, cancellation) =>
        {
            attempts++;
            received = email == "fixture-dialog@example.invalid" && password == "fixture-dialog-only";
            requestCancellation = cancellation;
            return pending.Task;
        };
        using var dialog = (Form)Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.NonPublic, null,
            new object[] { work }, null)!;
        dialog.Show();
        var context = uiContext!;
        SynchronizationContext.SetSynchronizationContext(context);
        var emailBox = Get<TextBox>(dialog, "_email");
        var passwordBox = Get<TextBox>(dialog, "_password");
        var submit = Get<Button>(dialog, "_signIn");
        var cancel = Get<Button>(dialog, "_cancel");
        emailBox.Text = "fixture-dialog@example.invalid";
        passwordBox.Text = "fixture-dialog-only";
        Check(passwordBox.UseSystemPasswordChar, "Zeta password field is masked with no show-secret control");
        foreach (var control in new Control[] { emailBox, passwordBox, submit, cancel })
            Check(control.Visible && dialog.ClientRectangle.Contains(dialog.RectangleToClient(control.RectangleToScreen(control.ClientRectangle))),
                "login dialog inputs/actions are accessible");
        submit.PerformClick();
        Check(received && attempts == 1 && passwordBox.Text == "" && !submit.Enabled &&
            !emailBox.Enabled && !passwordBox.Enabled, "submission passes only user-entered fixture values and immediately clears/disables credentials");
        submit.PerformClick();
        Check(attempts == 1, "busy login dialog cannot submit twice");
        cancel.PerformClick();
        Check(requestCancellation.IsCancellationRequested, "Cancel cancels in-flight login instead of starting another request");
        pending.SetCanceled(requestCancellation);
        UiUntil(() => !Get<bool>(dialog, "_busy"));
        Check(passwordBox.Text == "" && Get<Label>(dialog, "_message").Text.Contains("cancelled"), "cancelled login clears password and displays explicit result");
        submit.PerformClick();
        Check(attempts == 1 && Get<Label>(dialog, "_message").Text.Contains("Enter your Zeta"), "retry requires password re-entry");
        pending = new TaskCompletionSource<string>();
        passwordBox.Text = "fixture-dialog-only";
        SynchronizationContext.SetSynchronizationContext(context);
        submit.PerformClick();
        pending.SetResult("2 fixture historical events synchronized. Live panel: UNKNOWN.");
        UiUntil(() => !Get<bool>(dialog, "_busy"));
        Check(attempts == 2 && passwordBox.Text == "" && Get<Label>(dialog, "_message").Text.Contains("UNKNOWN"),
            "completed manual login displays sanitized result and does not retain password");
        emailBox.Clear();
        using var capture = new Bitmap(dialog.Width, dialog.Height);
        dialog.DrawToBitmap(capture, new Rectangle(Point.Empty, dialog.Size));
        capture.Save(Path.Combine(evidence, "manual-zeta-login-dialog-fixture.png"));
        dialog.Close();
        SynchronizationContext.SetSynchronizationContext(context);
    }

    private static async Task Client()
    {
        using var handler = new Handler();
        using var client = new ZetaCloudEventClient(handler);
        var settings = Settings();
        settings.Validate();
        foreach (var seconds in new[] { 1, 29, 571 })
        {
            var bad = settings.Copy();
            bad.PollSeconds = seconds;
            await Throws<InvalidDataException>(() => { bad.Validate(); return Task.CompletedTask; }, "cloud polling outside validated bounds rejected");
        }
        var shortFreshness = settings.Copy();
        shortFreshness.FreshnessSeconds = 39;
        await Throws<InvalidDataException>(() => { shortFreshness.Validate(); return Task.CompletedTask; }, "freshness must cover actual poll plus timeout");
        Check(settings.PollSeconds == 30 && settings.TimeoutMilliseconds == 10000 && settings.FreshnessSeconds == 90, "cloud timing defaults");
        var valid = await client.GetAsync(settings, Token, false, CancellationToken.None);
        Check(valid.Success && valid.Authenticated && valid.Events.Count == 1, "valid fixture event response");
        var today = DateTime.UtcNow.Date;
        Check(handler.LastUri == $"https://www.zetaremote.com/api//v2/sites/78392d300758/events?start={today.AddDays(-30):yyyy-MM-dd}&end={today:yyyy-MM-dd}", "exact observed double-slash route and rolling UTC date query");
        Check(handler.LastMethod == HttpMethod.Get && handler.LastAuthorization == "Bearer " + Handler.PanelToken,
            "GET events uses exchanged panel token, never account token");
        Check(handler.AuthRequests == 1 && handler.LastAuthAuthorization == "Bearer " + Token && handler.LastAuthBody == "{}",
            "approved claims/auth POST sends account token and exact empty JSON object");
        var record = valid.Events[0];
        Check(record.TimestampUtc == DateTimeOffset.Parse("2026-11-04T08:00:00Z") && record.Zone == "2" && record.DeviceAddress == "L1-A2", "only returned fields with explicit timezone normalized");
        handler.Status = HttpStatusCode.Unauthorized;
        var connectivity = await client.GetAsync(settings, "", true, CancellationToken.None);
        Check(!connectivity.Success && connectivity.TransportSucceeded && !connectivity.Authenticated && connectivity.HttpStatus == 401 &&
            !connectivity.CredentialRenewalRequired && handler.LastAuthorization == "" && connectivity.ErrorMessage.Contains("NOT verified"),
            "transport HTTP401 is not a successful connection or saved-token authentication test");
        foreach (var code in new[] { 401, 403, 404, 429, 500, 503, 302 })
        {
            handler.Status = (HttpStatusCode)code;
            handler.RetryAfter = code == 429 ? 120 : null;
            var result = await client.GetAsync(settings, Token, false, CancellationToken.None);
            Check(!result.Success && !result.Authenticated && result.HttpStatus == code && result.ErrorMessage.Length > 0, $"HTTP {code} cannot succeed");
            Check(!result.ErrorMessage.Contains(Token), $"HTTP {code} does not expose credentials or body");
            if (code == 429) Check(result.RetryAfterSeconds >= 120, "Retry-After respected");
        }
        handler.Status = HttpStatusCode.OK;
        handler.RetryAfter = null;
        foreach (var body in new[] { "<html>login</html>", "{", "{}", """{"events":{}}""", """{"events":[{}]}""",
            """{"events":[{"id":{},"message":"x"}]}""", """{"events":[{"id":"x","timestamp":"2026-11-04T08:00:00"}]}""" })
        {
            handler.Body = body;
            var result = await client.GetAsync(settings, Token, false, CancellationToken.None);
            Check(!result.Success && !result.Authenticated && result.Events.Count == 0, "malformed/unsupported schema fails closed");
        }
        handler.ContentType = "text/html";
        handler.Body = EventJson;
        Check(!(await client.GetAsync(settings, Token, false, CancellationToken.None)).Success, "HTML content type rejected even with JSON body");
        handler.ContentType = "application/json";
        handler.Body = """[{"message":"unknown category and metadata omitted"}]""";
        var unknown = await client.GetAsync(settings, Token, false, CancellationToken.None);
        Check(unknown.Success && unknown.Events[0].Zone == "" && unknown.Events[0].EventType == "" && unknown.Events[0].TimestampUtc is null, "missing fields not invented");
        handler.Body = new string(' ', 2 * 1024 * 1024 + 1);
        Check(!(await client.GetAsync(settings, Token, false, CancellationToken.None)).Success, "response body safety limit");
        var original = settings.CloudUrl;
        foreach (var host in new[] { "https://evil.example", "http://www.zetaremote.com", "https://www.zetaremote.com.evil.example", "https://user@www.zetaremote.com", "https://www.zetaremote.com:443" })
        {
            settings.CloudUrl = host;
            await Throws<InvalidDataException>(() => client.GetAsync(settings, Token, false, CancellationToken.None), "unapproved host rejected before request");
        }
        settings.CloudUrl = original;
        settings.CloudApiEndpoint = "/api/v2/sites/{site_id}/events";
        await Throws<InvalidDataException>(() => client.GetAsync(settings, Token, false, CancellationToken.None), "normalized single-slash route rejected");
        settings.CloudApiEndpoint = ZetaCloudEventClient.EventRoute;
        settings.CloudSiteIdentifier = "../other";
        await Throws<InvalidDataException>(() => client.GetAsync(settings, Token, false, CancellationToken.None), "site traversal rejected");
        settings.CloudSiteIdentifier = "78392d300758";
        foreach (var secret in new[] { "", "   ", "Bearer", "Bearer Bearer", "bad token", "secret\r\nInjected: yes" })
        {
            var requests = handler.Requests;
            var rejected = await client.GetAsync(settings, secret, false, CancellationToken.None);
            Check(!rejected.Success && rejected.HttpStatus is null && rejected.ErrorOrigin == "LOCAL" &&
                rejected.CredentialRenewalRequired && handler.Requests == requests, "malformed local credentials never produce an HTTP request");
        }
        handler.Body = EventJson;
        handler.Delay = 2000;
        settings.TimeoutMilliseconds = 250;
        var timeout = await client.GetAsync(settings, Token, false, CancellationToken.None);
        Check(!timeout.Success && timeout.Stage == "TIMEOUT", "timeout classified");
        using var cancelled = new CancellationTokenSource(30);
        await Throws<OperationCanceledException>(() => client.GetAsync(settings, Token, false, cancelled.Token), "caller cancellation propagated");
        handler.Delay = 0;
        handler.Failure = new HttpRequestException(HttpRequestError.SecureConnectionError, "fixture TLS detail");
        Check((await client.GetAsync(settings, Token, false, CancellationToken.None)).Stage == "TLS FAILURE", "TLS failures safely classified");
        handler.Failure = new HttpRequestException("fixture DNS detail", new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.HostNotFound));
        Check((await client.GetAsync(settings, Token, false, CancellationToken.None)).Stage == "DNS FAILURE", "DNS failures safely classified");
        handler.Failure = new HttpRequestException(HttpRequestError.NameResolutionError, "fixture DNS detail without socket");
        Check((await client.GetAsync(settings, Token, false, CancellationToken.None)).Stage == "DNS FAILURE", "framework DNS classification supported without inner socket");
    }

    private static async Task Persistence(string root)
    {
        var store = new FireConnectionStore(Path.Combine(root, "store.json"));
        var settings = Settings();
        store.Save(settings, "legacy-user", "legacy-password", false, Admin);
        Check(store.Load().ProtectedCloudToken.Length == 0, "old Cloud password is not a new token");
        store.Save(settings, "", "", false, Admin, cloudToken: Token);
        var document = store.Load();
        Check(document.FormatVersion == 3 && document.ProtectedCloudToken.Length > 0 && !File.ReadAllText(store.FilePath).Contains(Token), "DPAPI token persisted without plaintext");
        store.Save(settings, "", "", false, Admin, cloudToken: "  Bearer Bearer " + Token + " ");
        Check((string)typeof(FireConnectionStore).GetMethod("Unprotect", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, new object[] { store.Load().ProtectedCloudToken })! == Token,
            "DPAPI stores only canonical token without duplicated header scheme");
        var savedToken = store.Load().ProtectedCloudToken;
        store.Save(settings, "", "", false, Admin, cloudToken: "");
        Check(store.Load().ProtectedCloudToken == savedToken, "blank replacement retains saved token");
        await Throws<InvalidDataException>(() =>
        {
            store.Save(settings, "", "", false, Admin, cloudToken: "   ");
            return Task.CompletedTask;
        }, "whitespace-only replacement is rejected instead of silently clearing or retaining an unexpected token");
        Check(store.Load().ProtectedCloudToken == savedToken, "rejected replacement cannot overwrite saved protected credentials");
        var previous = document.ProtectedPassword;
        using var handler = new Handler();
        using (var service = new FireConnectionService(store, cloudClient: new ZetaCloudEventClient(handler)))
        {
            var result = await service.TestCloudAsync(settings, false, true, Admin);
            Check(result.Success && service.CloudEventCount(settings.CloudSiteIdentifier) == 1, "saved token decrypts for authorized request and event commit");
            Check(service.ReadMapEventEvidence().Count == 1 && service.ReadMapEventEvidence()[0].Address == "L1-A2" &&
                  service.ReadMapEventEvidence()[0].SiteId == settings.CloudSiteIdentifier,
                "map consumes exact-address immutable evidence from successful saved-site synchronization without reading history on UI ticks");
            Check(service.ReadLatchedMapEvents().Count == 1 && service.ReadLatchedMapEvents()[0].Signal == MapSignal.Fire,
                "successful saved-site event synchronization latches historical fire separately from live state");
            service.ResetMapAlerts(Admin, service.ReadLatchedMapEvents());
            Check(service.ReadLatchedMapEvents().Count == 0, "authorized local reset clears pending events");
            await service.TestCloudAsync(settings, false, true, Admin);
            Check(service.ReadLatchedMapEvents().Count == 0, "overlapping cloud polling cannot relatch reset event IDs");
            var migrationStore = new FireConnectionStore(Path.Combine(root, "map-history-migration.json"));
            migrationStore.Save(settings, "", "", false, Admin);
            File.Copy(store.FilePath + ".cloud-events.db", migrationStore.FilePath + ".cloud-events.db");
            using (var migration = new FireConnectionService(migrationStore, allowCommunication: false))
            {
                await migration.PrepareMapEventHistoryAsync();
                Check(migration.ReadLatchedMapEvents().Count == 1 && migration.ReadLatchedMapEvents()[0].Signal == MapSignal.Fire,
                    "existing stored events populate map latches without network access or live-state promotion");
                IReadOnlyList<FireMapEventEvidence> LocationEvidence() =>
                    (IReadOnlyList<FireMapEventEvidence>)typeof(FireConnectionService).GetMethod("ReadMapLocationEvidence", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(migration, null)!;
                var locationEvidence = LocationEvidence();
                Check(locationEvidence.Count == 1 && ReferenceEquals(locationEvidence, LocationEvidence()) && migration.ReadMapEventEvidence().Count == 0,
                    "background history hydration publishes lock-free location hints separately from synchronized/current evidence");
                migration.ResetMapAlerts(Admin, migration.ReadLatchedMapEvents());
                Check(migration.ReadLatchedMapEvents().Count == 0 && LocationEvidence().Count == 1,
                    "resetting a map alarm does not erase historical location knowledge");
                var otherSource = settings.Copy();
                otherSource.CloudUrl = "https://another-fixture.example";
                typeof(FireConnectionService).GetMethod("PublishMapLocationEvidence", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(migration, new object[] { otherSource, migration.ReadCloudEvents(settings.CloudSiteIdentifier) });
                Check(LocationEvidence().Count == 0, "same-site location hints cannot cross cloud-source boundary");
            }
            await Throws<InvalidOperationException>(() => service.ExportDiagnosticsAsync(store.FilePath + ".map-alerts"),
                "diagnostic export cannot overwrite persistent local reset state");
            var prefixedDocument = store.Load();
            prefixedDocument.ProtectedCloudToken = (string)typeof(FireConnectionStore).GetMethod("Protect", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, new object[] { " Bearer Bearer fixture-legacy-prefixed-account " })!;
            File.WriteAllText(store.FilePath, JsonSerializer.Serialize(prefixedDocument));
            await service.TestCloudAsync(settings, false, false, Admin);
            Check(handler.LastAuthAuthorization == "Bearer fixture-legacy-prefixed-account",
                "request normalization also handles older DPAPI tokens that contain a header prefix");
            store.Save(settings, "", "", false, Admin, cloudToken: "Bearer fixture-updated-saved-account");
            await service.TestCloudAsync(settings, false, false, Admin);
            Check(handler.LastAuthAuthorization == "Bearer fixture-updated-saved-account", "request reloads latest saved DPAPI token instead of stale cached token");
            store.Save(settings, "", "", false, Admin, cloudToken: Token);
            await service.TestCloudAsync(settings, false, true, Admin);
            Check(service.CloudEventCount(settings.CloudSiteIdentifier) == 1, "repeated overlapping window deduplicated");
            handler.Body = """[{"message":"fingerprint fixture","timestamp":"2026-11-04T08:01:00Z"},{"message":"fingerprint fixture","timestamp":"2026-11-04T08:01:00Z"}]""";
            await service.TestCloudAsync(settings, false, true, Admin);
            Check(service.CloudEventCount(settings.CloudSiteIdentifier) == 2, "stable no-ID fingerprint deduplicates");
            Check(service.ReadMapEventEvidence().Count == 1 && service.ReadMapEventEvidence()[0].Address == "",
                "unaddressed events reach map review without fabricated addresses");
            handler.Body = EventJson;
            var secondSite = settings.Copy();
            secondSite.CloudSiteIdentifier = "other-fixture-site";
            await service.TestCloudAsync(secondSite, false, true, Admin);
            Check(service.CloudEventCount(secondSite.CloudSiteIdentifier) == 1, "site scopes vendor identity independently");
            Check(service.ReadStatus().LastEventUtc == DateTimeOffset.Parse("2026-11-04T08:01:00Z"), "draft tests for another site cannot overwrite saved-site monitoring evidence");
            Check(service.ReadMapEventEvidence().Count == 1 && service.ReadMapEventEvidence()[0].SiteId == settings.CloudSiteIdentifier,
                "draft site test cannot replace the map's saved-site event evidence");
            Check(service.ReadLatchedMapEvents().Count == 0, "draft site retrieval cannot latch another site's events on saved map");
            var before = service.CloudEventCount(settings.CloudSiteIdentifier);
            await service.ClearDiagnosticLogsAsync(Admin);
            Check(service.CloudEventCount(settings.CloudSiteIdentifier) == before, "clear diagnostics never removes historical events");
            Check(!service.DiagnosticExport().Contains(Token), "safe diagnostic export excludes token");
            await Throws<InvalidOperationException>(() => service.ExportDiagnosticsAsync(store.FilePath + ".cloud-events.db"), "diagnostic export cannot overwrite history");
        }

        using (var reloaded = new FireConnectionService(store, cloudClient: new ZetaCloudEventClient(new Handler())))
        {
            Check(reloaded.ReadStatus().HasCloudToken && reloaded.CloudEventCount(settings.CloudSiteIdentifier) == 2, "token and deduplicated history survive service restart");
            Check(reloaded.ReadMapEventEvidence().Count == 0, "service restart does not promote persisted historical fire records into current map state");
            Check(reloaded.ReadLatchedMapEvents().Count == 0, "service restart retains locally reset event identities");
            await reloaded.PrepareMapEventHistoryAsync();
            Check(reloaded.ReadLatchedMapEvents().Count == 0, "background history hydration cannot undo a persisted local reset");
            Check(reloaded.ReadCloudEvents(settings.CloudSiteIdentifier).Any(e => e.Message == "fixture historical event"), "protected event payload restores original validated fields");
            using var raw = new FileStream(store.FilePath + ".cloud-events.db", FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var copy = new MemoryStream();
            raw.CopyTo(copy);
            var bytes = Encoding.UTF8.GetString(copy.ToArray());
            Check(!bytes.Contains("fixture historical event") && !bytes.Contains("fixture detector"), "event payload not present as plaintext in SQLite");
        }
        store.Save(settings, "", "", false, Admin, clearCloudToken: true);
        Check(store.Load().ProtectedCloudToken.Length == 0 && store.Load().ProtectedPassword == previous, "clearing cloud token retains legacy credentials");
        using var missing = new FireConnectionService(store, cloudClient: new ZetaCloudEventClient(new Handler()));
        Check(missing.ReadStatus().State == "NOT CONFIGURED", "missing token explicitly NOT CONFIGURED");
        await Throws<InvalidOperationException>(() => missing.ConnectAsync(Admin), "missing token cannot connect using legacy password");
        var local = await missing.TestCloudAsync(settings, false, true, Admin);
        Check(local.HttpStatus is null && local.Stage == "LOCAL CREDENTIAL" && local.ErrorOrigin == "LOCAL" &&
            missing.ReadStatus().CloudCredentialRenewalRequired && missing.ReadStatus().TransportHealth == "DISCONNECTED",
            "missing saved token is a local credential failure, never a Zeta HTTP401");
        using var manager = new FirePanelManager(missing);
        var panel = manager.AddPanel("archived fixture", Admin);
        panel.Service.ReadCloudEvents(settings.CloudSiteIdentifier);
        var historyPath = panel.Service.StoreFilePath + ".cloud-events.db";
        await manager.RemoveAsync(panel, Admin);
        var replacement = manager.AddPanel("new fixture", Admin);
        Check(File.Exists(historyPath) && replacement.Id != panel.Id, "panel removal retains archive and prevents reassignment of historical panel ID");
    }

    private static async Task ObservedContract()
    {
        var clock = new FixtureClock();
        using var handler = new Handler { Body = ObservedEvents };
        using var client = new ZetaCloudEventClient(handler, clock);
        var settings = Settings();
        var result = await client.GetAsync(settings, "  bEaReR Bearer " + Token + "  ", false, CancellationToken.None);
        Check(result.Success && handler.LastAuthAuthorization == "Bearer " + Token &&
            handler.LastAuthorization == "Bearer " + Handler.PanelToken, "duplicate case-insensitive schemes normalize to exactly one outgoing header");
        Check(handler.LastUri == "https://www.zetaremote.com/api//v2/sites/78392d300758/events?start=2026-10-05&end=2026-11-04",
            "observed URL reproduced exactly, using an ampersand, not HTML amp encoding");
        Check(result.Events.Count == 2 && result.Events[0].EventId == "991214" &&
            result.Events[0].TimestampUtc == DateTimeOffset.Parse("2026-11-03T15:20:00Z") &&
            result.Events[0].EventType == "fault" && result.Events[0].Message == "Fixture fault with 2, Zone Monitor Module" &&
            result.Events[0].Description == "Fixture area (Address 2)" && result.Events[0].Zone == "" && result.Events[0].DeviceAddress == "",
            "observed fields map exactly without inferring zone/address from title text");
        clock.Advance(TimeSpan.FromDays(1));
        handler.AuthBody = JsonSerializer.Serialize(new { token = Handler.PanelToken, expires = clock.GetUtcNow().AddMinutes(15).ToUnixTimeSeconds() });
        result = await client.GetAsync(settings, Token, false, CancellationToken.None);
        Check(result.Success && handler.LastUri.EndsWith("?start=2026-10-06&end=2026-11-05"), "UTC date window advances without discarding local history");
        foreach (var body in new[] {
            ObservedEvents.Replace("78392d300758", "other-site"),
            ObservedEvents.Replace("\"date\":", "\"missing-date\":"),
            ObservedEvents.Replace("\"category\":", "\"missing-category\":"),
            ObservedEvents.Replace("\"title\":", "\"missing-title\":"),
            ObservedEvents.Replace("2026-11-03T15:20:00.000Z", "not-a-date") })
        {
            handler.Body = body;
            result = await client.GetAsync(settings, Token, false, CancellationToken.None);
            Check(!result.Success && result.Events.Count == 0 && result.ErrorOrigin == "RESPONSE VALIDATION",
                "observed schema errors/site mismatch reject complete response before synchronization");
        }
        handler.Body = ObservedEvents;
        // The vendor, not an undocumented local JWT policy, decides account token claims/signature validity.
        foreach (var claims in new object[] {
            new { exp = 1, iss = "fixture-expired", aud = "fixture-audience", scope = "fixture" },
            new { exp = "invalid-expiry", iss = 42, aud = false, scope = new[] { "invalid-scope" } },
            new { unexpected = "fixture-missing-claims" } })
        {
            var jwt = Encode("""{"alg":"HS256","typ":"JWT"}""") + "." + Encode(JsonSerializer.Serialize(claims)) + ".fixture-signature";
            handler.AuthStatus = HttpStatusCode.Unauthorized;
            handler.AuthBody = """{"error":"invalid_token","message":"fixture secret must never be logged"}""";
            var events = handler.EventRequests;
            result = await client.GetAsync(settings, jwt, false, CancellationToken.None);
            Check(!result.Success && handler.LastAuthAuthorization == "Bearer " + jwt && handler.EventRequests == events &&
                result.HttpStatus == 401 && result.ErrorOrigin == "ZETA HTTP" && result.Stage == "CLAIMS AUTHORIZATION",
                "expired/malformed/missing JWT claims are not locally misclassified as vendor401");
            Check(result.ServerErrorCategory == "invalid_token" && result.CredentialRenewalRequired &&
                !result.ErrorMessage.Contains(jwt) && !result.ErrorMessage.Contains("fixture secret"),
                "safe category retained, raw server message and credentials excluded");
        }
        foreach (var code in new[] { 401, 403 })
        {
            client.InvalidateAuthorization();
            handler.AuthStatus = (HttpStatusCode)code;
            handler.AuthBody = """{"error":"fixture-private-arbitrary-category","token":"fixture-secret"}""";
            result = await client.GetAsync(settings, Token, false, CancellationToken.None);
            Check(result.HttpStatus == code && result.ServerErrorCategory == "NOT PROVIDED / NOT ALLOWLISTED" &&
                !result.ErrorMessage.Contains("fixture-"), "unknown server categories and echoed secrets are never copied to diagnostics");
            handler.Challenge = """error="expired_token", error_description="fixture private text" """;
            result = await client.GetAsync(settings, Token, false, CancellationToken.None);
            Check(result.ServerErrorCategory == "expired_token" && !result.ErrorMessage.Contains("private text"), "allowlisted WWW-Authenticate category only");
            handler.Challenge = null;
        }
        handler.AuthBody = """{"error":{"name":"TokenExpiredError","message":"fixture confidential JWT detail"}}""";
        result = await client.GetAsync(settings, Token, false, CancellationToken.None);
        Check(result.ServerErrorCategory == "tokenexpirederror" && !result.ErrorMessage.Contains("confidential"),
            "nested allowlisted server expiry category is retained without copying JWT error detail");
        foreach (var error in new[] {
            """{"err":"invalid_token"}""",
            """{"err":{"name":"TokenExpiredError","message":"fixture confidential token detail"}}""",
            """{"err":"fixture confidential arbitrary error"}""" })
        {
            handler.AuthBody = error;
            result = await client.GetAsync(settings, Token, false, CancellationToken.None);
            var expected = error.Contains("TokenExpiredError") ? "tokenexpirederror" :
                error.Contains("invalid_token") ? "invalid_token" : "NOT PROVIDED / NOT ALLOWLISTED";
            Check(result.HttpStatus == 403 && result.Stage == "CLAIMS AUTHORIZATION" &&
                result.ServerErrorCategory == expected && !result.ErrorMessage.Contains("confidential"),
                "observed APICall err field is read safely without logging arbitrary server strings");
        }
        foreach (var errorBody in new[] { "{", "<html>fixture confidential login page</html>", new string('x', 65537) })
        {
            handler.AuthBody = errorBody;
            result = await client.GetAsync(settings, Token, false, CancellationToken.None);
            Check(result.HttpStatus == 403 && result.ErrorOrigin == "ZETA HTTP" && result.Stage == "CLAIMS AUTHORIZATION" &&
                result.CredentialRenewalRequired && result.ServerErrorCategory == "UNAVAILABLE (ERROR BODY NOT ACCEPTED)" &&
                !result.ErrorMessage.Contains("confidential"), "unreadable error bodies cannot mask original server status/stage or leak body text");
        }
        handler.AuthStatus = HttpStatusCode.OK;
        handler.AuthBody = JsonSerializer.Serialize(new { token = "Bearer Bearer " + Handler.PanelToken, expires = clock.GetUtcNow().AddMinutes(15).ToUnixTimeSeconds() });
        handler.Status = HttpStatusCode.Forbidden;
        handler.Body = """{"code":"insufficient_scope"}""";
        result = await client.GetAsync(settings, Token, false, CancellationToken.None);
        Check(result.Stage == "EVENT REQUEST" && result.HttpStatus == 403 && result.ServerErrorCategory == "insufficient_scope" &&
            result.CredentialRenewalRequired && result.SessionExpiresUtc is null, "event403 distinguished from claims failure and invalidates session");
        handler.Status = HttpStatusCode.OK;
        handler.Body = ObservedEvents;
        result = await client.GetAsync(settings, Token, false, CancellationToken.None);
        Check(result.Success && result.Authenticated && result.Stage == "VALID EVENTS" && handler.LastAuthorization == "Bearer " + Handler.PanelToken,
            "only parsed authorized event response succeeds; exchanged tokens also have a single Bearer scheme");
        static string Encode(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static async Task Authorization()
    {
        var clock = new FixtureClock();
        using var handler = new Handler();
        using var client = new ZetaCloudEventClient(handler, clock);
        var settings = Settings();
        handler.AuthBody = JsonSerializer.Serialize(new { token = Handler.PanelToken, expires = clock.GetUtcNow().AddMinutes(15).ToUnixTimeSeconds() });
        var result = await client.GetAsync(settings, Token, false, CancellationToken.None);
        Check(result.Success && result.SessionExpiresUtc == clock.GetUtcNow().AddMinutes(15), "validated server expiry is exposed without exposing session token");
        await client.GetAsync(settings, Token, false, CancellationToken.None);
        Check(handler.AuthRequests == 1 && handler.EventRequests == 2, "valid panel session reused instead of authenticating every poll");
        clock.Advance(TimeSpan.FromSeconds(869));
        await client.GetAsync(settings, Token, false, CancellationToken.None);
        Check(handler.AuthRequests == 1, "session with 31 seconds remaining is reused");
        clock.Advance(TimeSpan.FromSeconds(1));
        handler.AuthBody = JsonSerializer.Serialize(new { token = "fixture-renewed-panel-token", expires = clock.GetUtcNow().AddMinutes(15).ToUnixTimeSeconds() });
        result = await client.GetAsync(settings, Token, false, CancellationToken.None);
        Check(result.Success && handler.AuthRequests == 2 && handler.LastAuthorization == "Bearer fixture-renewed-panel-token",
            "session renews exactly at 30-second safety boundary using returned replacement token");
        await client.GetAsync(settings, "fixture-new-account-token", false, CancellationToken.None);
        Check(handler.AuthRequests == 3 && handler.LastAuthAuthorization == "Bearer fixture-new-account-token", "account token replacement invalidates session identity");
        settings.CloudSiteIdentifier = "fixture-other-site";
        await client.GetAsync(settings, "fixture-new-account-token", false, CancellationToken.None);
        Check(handler.AuthRequests == 4 && handler.LastUri.Contains("/fixture-other-site/events"), "site switch requires a new authorization exchange");
        client.InvalidateAuthorization();
        await client.GetAsync(settings, "fixture-new-account-token", false, CancellationToken.None);
        Check(handler.AuthRequests == 5, "explicit credential/settings invalidation forces exchange");
        var before = handler.AuthRequests;
        await client.GetAsync(settings, "", true, CancellationToken.None);
        Check(handler.AuthRequests == before && handler.LastAuthorization == "", "transport-only test never sends either credential or exchanges a token");
        client.InvalidateAuthorization();
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => client.GetAsync(settings, Token, false, CancellationToken.None)));
        Check(handler.AuthRequests == before + 1 && handler.MaximumActive == 1, "parallel requests serialize and share one session exchange");
        foreach (var status in new[] { 401, 403, 404, 429, 500, 302 })
        {
            client.InvalidateAuthorization();
            handler.AuthStatus = (HttpStatusCode)status;
            handler.RetryAfter = status == 429 ? 120 : null;
            var events = handler.EventRequests;
            result = await client.GetAsync(settings, Token, false, CancellationToken.None);
            Check(!result.Success && !result.Authenticated && result.Stage == "CLAIMS AUTHORIZATION" &&
                result.HttpStatus == status && handler.EventRequests == events, $"auth HTTP {status} cannot fall through to events or success");
            Check(!result.ErrorMessage.Contains(Token) && !result.ErrorMessage.Contains(Handler.PanelToken), $"auth HTTP {status} errors exclude credentials");
            if (status == 429) Check(result.RetryAfterSeconds >= 120, "auth exchange propagates shared Retry-After");
        }
        handler.AuthStatus = HttpStatusCode.OK;
        handler.RetryAfter = null;
        foreach (var body in new[] { "{}", "{", """{"token":"fixture-session"}""", """{"expires":1793796179}""",
                """{"token":"","expires":253402300800}""", """{"token":{},"expires":253402300799}""",
                """{"token":"fixture-session","expires":"253402300799"}""",
                """{"token":"fixture-session","expires":253402300800}""",
                """{"token":"fixture-session","expires":-1}""",
                JsonSerializer.Serialize(new { token = "fixture-session", expires = clock.GetUtcNow().ToUnixTimeSeconds() }),
                JsonSerializer.Serialize(new { token = "fixture-session", expires = clock.GetUtcNow().AddSeconds(30).ToUnixTimeSeconds() }),
                new string(' ', 65537) })
        {
            client.InvalidateAuthorization();
            handler.AuthBody = body;
            var events = handler.EventRequests;
            result = await client.GetAsync(settings, Token, false, CancellationToken.None);
            Check(!result.Success && !result.Authenticated && result.SessionExpiresUtc is null &&
                handler.EventRequests == events, "malformed, missing, expired or oversized claims response fails closed");
        }
        handler.AuthBody = JsonSerializer.Serialize(new { token = Handler.PanelToken, expires = clock.GetUtcNow().AddMinutes(15).ToUnixTimeSeconds() });
        handler.Status = HttpStatusCode.Unauthorized;
        before = handler.AuthRequests;
        result = await client.GetAsync(settings, Token, false, CancellationToken.None);
        Check(!result.Success && result.SessionExpiresUtc is null && handler.AuthRequests == before + 1,
            "event HTTP401 invalidates accepted session without an immediate authentication retry loop");
        handler.Status = HttpStatusCode.OK;
        result = await client.GetAsync(settings, Token, false, CancellationToken.None);
        Check(result.Success && handler.AuthRequests == before + 2, "next explicit request exchanges again after event authentication failure");
        client.InvalidateAuthorization();
        handler.Delay = 10000;
        using var cancellation = new CancellationTokenSource(30);
        await Throws<OperationCanceledException>(() => client.GetAsync(settings, Token, false, cancellation.Token),
            "cancellation interrupts claims exchange, not just event GET");
    }

    private sealed class FixtureClock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => now;
        internal void Advance(TimeSpan amount) => now += amount;
    }

    private static async Task AuthenticationFailures(string root)
    {
        var settings = Settings();
        settings.AutoReconnect = true;
        var store = new FireConnectionStore(Path.Combine(root, "auth-failures", "fire.json"));
        store.Save(settings, "", "", false, Admin, cloudToken: Token);
        using var handler = new Handler { Body = ObservedEvents };
        using var service = new FireConnectionService(store, cloudClient: new ZetaCloudEventClient(handler));
        var accepted = await service.TestCloudAsync(settings, false, true, Admin);
        Check(accepted.Success && service.CloudEventCount(settings.CloudSiteIdentifier) == 2 &&
            service.ReadCloudEvents(settings.CloudSiteIdentifier).Any(e => e.EventType == "service" && e.Message == "Fixture Reset button pressed"),
            "observed authorized payload is persisted and reloaded with date/category/title/location");
        var history = service.ReadCloudEvents(settings.CloudSiteIdentifier).Select(record => JsonSerializer.Serialize(record)).ToArray();
        foreach (var stage in new[] { "CLAIMS AUTHORIZATION", "EVENT REQUEST" })
        {
            foreach (var code in new[] { 401, 403 })
            {
                await service.SaveAsync(settings, "", "", false, Admin);
                handler.AuthStatus = stage == "CLAIMS AUTHORIZATION" ? (HttpStatusCode)code : HttpStatusCode.OK;
                handler.Status = stage == "EVENT REQUEST" ? (HttpStatusCode)code : HttpStatusCode.OK;
                handler.AuthBody = stage == "CLAIMS AUTHORIZATION" ? """{"error":"invalid_token"}""" : null;
                handler.Body = """{"error":"access_denied","message":"fixture confidential detail"}""";
                var requests = handler.Requests;
                var events = handler.EventRequests;
                await service.ConnectAsync(Admin);
                await Until(() => Get<Task>(service, "_worker").IsCompleted);
                var snapshot = service.ReadStatus();
                Check(!snapshot.Monitoring && snapshot.State == "AUTHENTICATION FAILED" &&
                    snapshot.TransportHealth == "DISCONNECTED" && snapshot.CloudTransportSucceeded &&
                    snapshot.CloudCredentialRenewalRequired && snapshot.CloudStage == stage && snapshot.HttpStatus == code &&
                    snapshot.CloudErrorOrigin == "ZETA HTTP", $"{stage} HTTP{code} stops worker and clears stale CONNECTING state");
                Check(handler.Requests == requests + (stage == "CLAIMS AUTHORIZATION" ? 1 : 2) &&
                    handler.EventRequests == events + (stage == "CLAIMS AUTHORIZATION" ? 0 : 1),
                    $"HTTP{code} never loops or retries authorization even with AutoReconnect enabled");
                await Task.Delay(150);
                Check(handler.Requests == requests + (stage == "CLAIMS AUTHORIZATION" ? 1 : 2),
                    "completed authentication-failed worker cannot dispatch more polling");
                var export = service.DiagnosticExport();
                Check(export.Contains(stage) && export.Contains("\"CredentialRenewalRequired\": true") &&
                    export.Contains("\"TransportResponseReceived\": true") && !export.Contains(Token) &&
                    !export.Contains(Handler.PanelToken) && !export.Contains("confidential detail"),
                    "sanitized diagnostics distinguish server failure, stage, response receipt and credential renewal");
                Check(history.SequenceEqual(service.ReadCloudEvents(settings.CloudSiteIdentifier).Select(record => JsonSerializer.Serialize(record))),
                    "authorization failures preserve previously synchronized event history");
            }
        }
        await service.SaveAsync(settings, "", "", false, Admin);
        var document = store.Load();
        document.ProtectedCloudToken = "fixture-invalid-dpapi-base64";
        File.WriteAllText(store.FilePath, JsonSerializer.Serialize(document));
        var before = handler.Requests;
        var local = await service.TestCloudAsync(settings, false, true, Admin);
        Check(local.Stage == "LOCAL CREDENTIAL STORAGE" && local.ErrorOrigin == "LOCAL" && local.HttpStatus is null &&
            !local.TransportSucceeded && local.CredentialRenewalRequired && handler.Requests == before &&
            service.ReadStatus().TransportHealth == "DISCONNECTED", "DPAPI/load rejection is local, with no fabricated HTTP status or request");
    }

    private static async Task Service(string root)
    {
        var settings = Settings();
        var store = new FireConnectionStore(Path.Combine(root, "service.json"));
        store.Save(settings, "", "", false, Admin, cloudToken: Token);
        var handler = new Handler { Delay = 100 };
        using var service = new FireConnectionService(store, cloudClient: new ZetaCloudEventClient(handler));
        var connection = await service.TestCloudAsync(settings, true, false, Admin);
        Check(connection.Success && service.ReadStatus().State != "ONLINE" && service.ReadStatus().LastDataUtc is null, "HTTPS reachability does not imply ONLINE or sync");
        handler.Status = HttpStatusCode.Unauthorized;
        await service.TestCloudAsync(settings, true, false, Admin);
        Check(service.ReadStatus().CloudAuthentication == "NOT TESTED", "unauthenticated transport HTTP401 is not a token test failure");
        handler.Status = HttpStatusCode.OK;
        await service.ConnectAsync(Admin);
        await Until(() => handler.Active > 0);
        var test = service.TestCloudAsync(settings, false, true, Admin);
        await test;
        Check(handler.MaximumActive == 1, "worker and manual tests never overlap requests");
        Check(service.ReadStatus().CloudSessionExpiresUtc.HasValue &&
            service.DiagnosticExport().Contains("SessionExpiresUtc") && !service.DiagnosticExport().Contains(Handler.PanelToken),
            "status and diagnostics expose session expiry but never its token");
        var acceptedExpiry = service.ReadStatus().CloudSessionExpiresUtc;
        var exchanges = handler.AuthRequests;
        await service.TestCloudAsync(settings, true, false, Admin);
        Check(service.ReadStatus().CloudSessionExpiresUtc == acceptedExpiry && handler.AuthRequests == exchanges,
            "transport-only test preserves accepted session expiry without using credentials");
        Check(service.ReadStatus().State == "ONLINE" && service.ReadStatus().LastDataUtc.HasValue, "only validated persisted event response becomes ONLINE");
        var snapshot = Get<FireConnectionSnapshot>(service, "_snapshot");
        snapshot.LastDataUtc = DateTime.UtcNow.AddSeconds(-settings.FreshnessSeconds - 1);
        Check(service.ReadStatus().State == "DEGRADED" && service.DiagnosticExport().Contains("UNKNOWN"), "exact freshness threshold produces DEGRADED, never fire NORMAL");
        snapshot.LastDataUtc = DateTime.UtcNow;
        var requests = handler.Requests;
        await Task.Delay(150);
        Check(handler.Requests == requests, "worker does not poll rapidly before configured interval");
        await service.DisconnectAsync(Admin);
        Check(!service.ReadStatus().Monitoring && service.ReadStatus().State == "OFFLINE", "disconnect halts only connector");
        exchanges = handler.AuthRequests;
        await service.SaveAsync(settings, "", "", false, Admin, cloudToken: Token);
        await service.TestCloudAsync(settings, false, true, Admin);
        Check(handler.AuthRequests == exchanges + 1, "Save invalidates cached authorization even when the account token value is unchanged");
        handler.Status = HttpStatusCode.ServiceUnavailable;
        var loss = await service.TestCloudAsync(settings, false, true, Admin);
        Check(!loss.Success && service.ReadStatus().State == "DEGRADED" && service.ReadStatus().ConsecutiveFailures > 0, "previously valid connector degraded on connection loss");
        foreach (var code in new[] { 401, 403 })
        {
            handler.Status = (HttpStatusCode)code;
            await service.ConnectAsync(Admin);
            await Until(() => !service.ReadStatus().Monitoring);
            Check(service.ReadStatus().State == "AUTHENTICATION FAILED", $"HTTP {code} stops polling until manual recovery");
        }
        handler.Status = HttpStatusCode.TooManyRequests;
        handler.RetryAfter = 120;
        await service.TestCloudAsync(settings, false, true, Admin);
        requests = handler.Requests;
        await Throws<InvalidOperationException>(() => service.TestCloudAsync(settings, false, true, Admin), "manual retry cannot bypass HTTP429 delay");
        Check(requests == handler.Requests, "no HTTP request during rate-limit cooldown");
        var method = typeof(FireConnectionService).GetMethod("CloudDelaySeconds", BindingFlags.Static | BindingFlags.NonPublic)!;
        Check((double)method.Invoke(null, new object[] { settings, 0, 0d })! >= 30, "minimum polling is 30 seconds");
        Check((double)method.Invoke(null, new object[] { settings, 3, 0d })! == 120, "exponential failure backoff");
        Check((double)method.Invoke(null, new object[] { settings, 1, 180d })! == 180, "server Retry-After dominates ordinary backoff");
        using var cancelHandler = new Handler { Delay = 10000 };
        using var cancelService = new FireConnectionService(store, cloudClient: new ZetaCloudEventClient(cancelHandler));
        await cancelService.ConnectAsync(Admin);
        await Until(() => cancelHandler.Active > 0);
        var watch = Stopwatch.StartNew();
        await cancelService.DisconnectAsync(Admin);
        Check(watch.ElapsedMilliseconds < 1000 && cancelHandler.Active == 0, "disconnect cancels in-flight request promptly");
        foreach (var actor in new[] { new UserSession { UserId = "reader", Username = "reader", RoleName = "Operator" }, new UserSession { UserId = "preview", Username = "preview", RoleName = "Administrator", IsOfflinePreview = true } })
            await Throws<UnauthorizedAccessException>(() => service.TestCloudAsync(settings, false, true, actor), "restricted session cannot send cloud tests");
        var startupStore = new FireConnectionStore(Path.Combine(root, "startup.json"));
        settings.AutoConnect = true;
        startupStore.Save(settings, "", "", false, Admin, cloudToken: Token);
        using var cadence = new Handler();
        using var automatic = new FireConnectionService(startupStore, cloudClient: new ZetaCloudEventClient(cadence));
        await automatic.StartAsync();
        await Until(() => cadence.EventRequests >= 2, 35000);
        var starts = cadence.Starts.ToArray();
        Check(Stopwatch.GetElapsedTime(starts[0], starts[1]).TotalSeconds >= 30, "actual background polling request starts are at least 30 seconds apart without any page");
        Check(cadence.LoginRequests == 0, "startup and continuous polling never automatically submit login credentials");
        await automatic.StopAsync();
        settings.AutoReconnect = false;
        startupStore.Save(settings, "", "", false, Admin, cloudToken: Token);
        using var noRecoveryHandler = new Handler { Status = HttpStatusCode.ServiceUnavailable };
        using var noRecovery = new FireConnectionService(startupStore, cloudClient: new ZetaCloudEventClient(noRecoveryHandler));
        await noRecovery.ConnectAsync(Admin);
        await Until(() => !noRecovery.ReadStatus().Monitoring);
        await Task.Delay(150);
        Check(noRecoveryHandler.AuthRequests == 1 && noRecoveryHandler.EventRequests == 1, "disabled automatic reconnection halts after first failed event request");
        using var rejectedHandler = new Handler { AuthStatus = HttpStatusCode.Unauthorized };
        using var rejected = new FireConnectionService(startupStore, cloudClient: new ZetaCloudEventClient(rejectedHandler));
        await rejected.ConnectAsync(Admin);
        await Until(() => !rejected.ReadStatus().Monitoring);
        Check(rejected.ReadStatus().State == "AUTHENTICATION FAILED" && rejectedHandler.EventRequests == 0,
            "claims HTTP401 stops existing worker without attempting event GET");
        rejectedHandler.AuthStatus = HttpStatusCode.TooManyRequests;
        rejectedHandler.RetryAfter = 120;
        var rejectedResult = await rejected.TestCloudAsync(settings, false, true, Admin);
        var rejectedRequests = rejectedHandler.Requests;
        await Throws<InvalidOperationException>(() => rejected.TestCloudAsync(settings, false, true, Admin),
            "claims HTTP429 uses same cooldown for manual requests");
        Check(rejectedResult.RetryAfterSeconds >= 120 && rejectedHandler.Requests == rejectedRequests,
            "claims Retry-After prevents real request dispatch during cooldown");
    }

    private static void Ui(string root, string evidence)
    {
        var store = new FireConnectionStore(Path.Combine(root, "ui", "fire-connection.json"));
        var settings = Settings();
        store.Save(settings, "", "", false, Admin, cloudToken: Token);
        var handler = new Handler();
        using var service = new FireConnectionService(store, cloudClient: new ZetaCloudEventClient(handler));
        using var form = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(0, 0) };
        using var view = new FireConnectionSettingsView(service, Admin) { Dock = DockStyle.Fill };
        form.Controls.Add(view);
        form.Show();
        view.SetPageActive(true);
        using var context = new WindowsFormsSynchronizationContext();
        uiContext = context;
        SynchronizationContext.SetSynchronizationContext(context);
        foreach (var size in new[] { new Size(1080, 520), new Size(1300, 650), new Size(1620, 830) })
        {
            form.ClientSize = size;
            Pump(100);
            var left = Get<TextBox>(view, "_site");
            var right = Get<TextBox>(view, "_token");
            Check(left.Width >= 200 && right.Width >= 200, $"readable inputs at shell-like viewport {size}");
            Check(left.PointToScreen(Point.Empty).X < right.PointToScreen(Point.Empty).X, $"two-column form at {size}");
            var columns = Get<TableLayoutPanel>(view, "_columns");
            Check(columns.Controls[0].Bottom <= columns.Height + 3 && columns.Controls[1].Bottom <= columns.Height + 3, $"cards not vertically clipped at {size}");
            Check(Get<Panel>(view, "_scroll").AutoScrollPosition.Y == 0, $"opening/resize does not hide top status at {size}");
            using var image = new Bitmap(form.ClientSize.Width, form.ClientSize.Height);
            form.DrawToBitmap(image, form.ClientRectangle);
            image.Save(Path.Combine(evidence, $"cloud-ui-{size.Width}x{size.Height}.png"));
            var scroll = Get<Panel>(view, "_scroll");
            scroll.AutoScrollPosition = new Point(0, 100000);
            Pump(30);
            var actions = Get<FlowLayoutPanel>(view, "_buttons");
            Check(actions.Parent!.Bottom <= view.ClientSize.Height && actions.Visible,
                $"settings actions stay docked outside scrolling content at {size}");
            scroll.AutoScrollPosition = Point.Empty;
        }
        Check(Get<TextBox>(view, "_token").Text == "" && Get<TextBox>(view, "_token").UseSystemPasswordChar, "stored token blank and masked on reopen");
        Check(Get<Label>(view, "_state").Text.Contains("UNKNOWN"), "live panel state explicitly UNKNOWN");
        Check(Get<Label>(view, "_tokenState").Text.Contains("DPAPI"), "protected token presence without disclosure");
        Check(handler.Requests == 0, "opening UI performs no API request or auto-connect");
        Get<TextBox>(view, "_siteName").Text = "Updated fixture site";
        Get<TextBox>(view, "_token").Text = "fixture-only-replacement";
        Get<CheckBox>(view, "_showToken").Checked = true;
        Check(!Get<TextBox>(view, "_token").UseSystemPasswordChar, "show/hide applies only to entered draft");
        SynchronizationContext.SetSynchronizationContext(context);
        Click(view, "save");
        UiUntil(() => !Get<bool>(view, "_busy"));
        Check(service.ReadSettings().CloudSiteName == "Updated fixture site" && Get<TextBox>(view, "_token").Text == "" &&
            Get<TextBox>(view, "_token").UseSystemPasswordChar, "Save button persists site and new token, clears and remasks draft");
        handler.Status = HttpStatusCode.Unauthorized;
        Click(view, "test-connection");
        UiUntil(() => !Get<bool>(view, "_busy"));
        Check(handler.LastAuthorization == "" && service.ReadStatus().State != "ONLINE", "UI HTTPS test does not send token or manufacture online");
        Check(Get<Label>(view, "_message").Text.Contains("HTTP 401") && Get<Label>(view, "_message").Text.Contains("NOT verified") &&
            !Get<Label>(view, "_message").Text.Contains("successful", StringComparison.OrdinalIgnoreCase),
            "transport UI explicitly reports401 and never claims authenticated success");
        handler.AuthStatus = HttpStatusCode.Unauthorized;
        handler.AuthBody = """{"error":"invalid_token","message":"fixture confidential detail"}""";
        Click(view, "test-auth");
        UiUntil(() => !Get<bool>(view, "_busy"));
        Check(Get<Label>(view, "_message").Text.Contains("CLAIMS AUTHORIZATION") &&
            Get<Label>(view, "_message").Text.Contains("invalid_token") &&
            Get<Label>(view, "_tokenState").Text.Contains("rejected authorization") &&
            service.ReadStatus().TransportHealth == "DISCONNECTED" &&
            !Get<Label>(view, "_message").Text.Contains("confidential"), "UI exposes sanitized remote failure stage and explicit credential renewal");
        handler.AuthStatus = HttpStatusCode.OK;
        handler.AuthBody = null;
        handler.Status = HttpStatusCode.OK;
        Click(view, "test-auth");
        UiUntil(() => !Get<bool>(view, "_busy"));
        Check(service.ReadStatus().CloudAuthentication == "AUTHORIZED RESPONSE VALIDATED" && service.ReadStatus().LastDataUtc is null, "UI authentication performs real fixture request without synchronizing history");
        Click(view, "test-events");
        UiUntil(() => !Get<bool>(view, "_busy"));
        Check(service.CloudEventCount(settings.CloudSiteIdentifier) == 1, "settings retrieval still persists events after history moved to historian");
        Check(Get<Label>(view, "_message").Text.Contains("2026-11-04"), "retrieval displays actual last event timestamp");
        var savedClipboard = Clipboard.ContainsText() ? Clipboard.GetText() : null;
        try
        {
            Click(view, "copy-diagnostics");
            var diagnostic = Clipboard.GetText();
            Check(diagnostic.Contains("CloudEventConnector") && diagnostic.Contains("UNKNOWN") &&
                !diagnostic.Contains(Token) && !diagnostic.Contains("fixture-only-replacement"), "Copy Diagnostics copies real service diagnostics without credentials");
            var actions = Get<FlowLayoutPanel>(view, "_buttons");
            Check(actions.Controls.IndexOfKey("copy-diagnostics") == actions.Controls.IndexOfKey("clear-diagnostics") + 1,
                "Copy Diagnostics is beside Clear Diagnostic Logs");
        }
        finally
        {
            if (savedClipboard is not null) Clipboard.SetText(savedClipboard);
            else Clipboard.Clear();
        }
        Click(view, "clear-diagnostics");
        UiUntil(() => !Get<bool>(view, "_busy"));
        Check(service.CloudEventCount(settings.CloudSiteIdentifier) == 1, "UI clear diagnostics retains persistent event history");
        handler.Delay = 10000;
        Click(view, "test-events");
        UiUntil(() => Get<bool>(view, "_busy") && handler.Active > 0);
        Click(view, "cancel");
        UiUntil(() => !Get<bool>(view, "_busy"));
        Check(Get<Label>(view, "_message").Text.Contains("cancelled"), "UI cancellation reports outcome and remains responsive");
        handler.Delay = 0;
        Click(view, "connect");
        UiUntil(() => service.ReadStatus().Monitoring && service.ReadStatus().State == "ONLINE");
        Click(view, "disconnect");
        UiUntil(() => !Get<bool>(view, "_busy"));
        Check(!service.ReadStatus().Monitoring, "UI Connect and Disconnect control existing service");
        Click(view, "reconnect");
        UiUntil(() => service.ReadStatus().Monitoring && service.ReadStatus().State == "ONLINE");
        Click(view, "disconnect");
        UiUntil(() => !Get<bool>(view, "_busy"));
        Check(!Descendants(view).OfType<TabControl>().Any() && !Descendants(view).OfType<DataGridView>().Any(),
            "settings no longer contain event, diagnostics or advanced tabs or history grid");
        var csv = typeof(FireEventsView).GetMethod("Csv", BindingFlags.Static | BindingFlags.NonPublic)!;
        Check((string)csv.Invoke(null, new object[] { "=HYPERLINK(\"fixture\")" })! == "\"'=HYPERLINK(\"\"fixture\"\")\"", "CSV quoting and spreadsheet formula neutralization");
        Check(((string)csv.Invoke(null, new object[] { "\t =formula" })!).StartsWith("\"'"), "CSV formula neutralization includes leading whitespace");
        var connection = Get<ComboBox>(view, "_connectionType");
        Check(connection.Items.Cast<string>().SequenceEqual(new[] { "Zeta Remote Cloud API", "Local Modbus TCP", "Zeta BMS" }),
            "one direct connection selector exposes Cloud, Local and BMS settings");
        foreach (var size in new[] { new Size(1300, 600), new Size(1300, 650), new Size(1620, 830) })
        {
            form.ClientSize = size;
            connection.SelectedIndex = 0;
            Pump(200);
            var primaryNames = new[] { "Test Connection", "Save Settings", "Connect", "Disconnect", "Reconnect", "Clear Diagnostic Logs", "Copy Diagnostics" };
            Rectangle[]? expected = null;
            foreach (var mode in new[] { 0, 1, 2 })
            {
                connection.SelectedIndex = mode;
                Pump(250);
                Control editor = mode == 0 ? view : Get<Dictionary<int, LegacyFireConnectionSettingsView>>(view, "_alternatives")[mode];
                var toolbar = mode == 0 ? Get<FlowLayoutPanel>(view, "_buttons") : Get<FlowLayoutPanel>(editor, "_buttons");
                var actual = primaryNames.Select(text => toolbar.Controls.OfType<Button>().Single(button => button.Text == text))
                    .Select(button => new Rectangle(form.PointToClient(button.PointToScreen(Point.Empty)), button.Size)).ToArray();
                if (expected is null) expected = actual;
                else Check(actual.SequenceEqual(expected), $"all seven primary action rectangles identical in mode {mode} at {size}");
                Check(actual.Select(rect => rect.Size).Distinct().Count() == 1, $"all primary actions same size in mode {mode} at {size}");
                var viewport = mode == 0 ? Get<Panel>(view, "_scroll") : Get<Panel>(editor, "_scroll");
                using var capture = new Bitmap(view.ClientSize.Width, view.ClientSize.Height);
                view.DrawToBitmap(capture, view.ClientRectangle);
                capture.Save(Path.Combine(evidence, $"uniform-mode-{mode}-{size.Width}x{size.Height}.png"));
                Check(!viewport.VerticalScroll.Visible && !viewport.HorizontalScroll.Visible,
                    $"all settings visible without scrolling in mode {mode} at {size}; content={viewport.DisplayRectangle}, viewport={viewport.ClientSize}, minimum={viewport.Controls[0].MinimumSize}, preferred={viewport.Controls[0].GetPreferredSize(new Size(viewport.Width, 0))}");
                foreach (var input in Descendants(viewport).Where(control => control.Visible &&
                    (control is TextBox || control is ComboBox || control is NumericUpDown || control is CheckBox)))
                {
                    var bounds = viewport.RectangleToClient(input.RectangleToScreen(input.ClientRectangle));
                    Check(viewport.ClientRectangle.Contains(bounds), $"input {input.GetType().Name} fully visible in mode {mode} at {size}");
                }
                foreach (var caption in Descendants(viewport).OfType<Label>().Where(label => label.Visible && label.Text.Length > 0))
                    Check(caption.Width > 0 && caption.Height > 0, $"caption '{caption.Text}' has visible area in mode {mode} at {size}");
                Check(viewport.Controls[0].Height == viewport.ClientSize.Height, $"settings surface fills the same available area in mode {mode} at {size}");
                if (mode == 2)
                {
                    var protocol = Get<ComboBox>(editor, "_bmsType");
                    protocol.SelectedIndex = 0;
                    Pump(300);
                    using var modbusCapture = new Bitmap(view.Width, view.Height);
                    view.DrawToBitmap(modbusCapture, view.ClientRectangle);
                    modbusCapture.Save(Path.Combine(evidence, $"uniform-bms-modbus-{size.Width}x{size.Height}.png"));
                    Check(!viewport.VerticalScroll.Visible && Get<NumericUpDown>(editor, "_unit").Visible &&
                        Get<NumericUpDown>(editor, "_address").Visible, $"BMS Modbus also fits without hiding settings at {size}: content={viewport.Controls[0].Height} viewport={viewport.ClientSize.Height}");
                    protocol.SelectedIndex = 1;
                    Pump(300);
                }
            }
        }
        connection.SelectedIndex = 1;
        Pump(100);
        var alternatives = Get<Dictionary<int, LegacyFireConnectionSettingsView>>(view, "_alternatives");
        var legacy = alternatives[1];
        Check(Get<ComboBox>(legacy, "_type").SelectedIndex == 0 && Get<ComboBox>(legacy, "_type").Parent is null, "Local selection shows local settings directly without nested connection selector");
        Check(service.ReadSettings().ConnectionType == FireConnectionType.ZetaCloud && !Get<Panel>(view, "_scroll").Visible,
            "changing visible connection type neither saves nor connects");
        connection.SelectedIndex = 2;
        Pump(100);
        Check(Get<ComboBox>(alternatives[2], "_type").SelectedIndex == 2 && Get<ComboBox>(alternatives[2], "_bmsType").Visible,
            "BMS selection exposes vendor and independently verified BMS Modbus options directly");
        using (var image = new Bitmap(form.ClientSize.Width, form.ClientSize.Height))
        {
            form.DrawToBitmap(image, form.ClientRectangle);
            image.Save(Path.Combine(evidence, "direct-bms-settings.png"));
        }
        connection.SelectedIndex = 1;
        var local = alternatives[1];
        Get<TextBox>(local, "_endpoint").Text = "192.0.2.123";
        Click(local, "save");
        UiUntil(() => !Get<bool>(local, "_busy"));
        Check(service.ReadSettings().ConnectionType == FireConnectionType.LocalModbus && service.ReadSettings().LocalHost == "192.0.2.123" &&
            service.ReadStatus().HasCloudToken, "direct Local Save applies only reviewed draft settings and retains cloud token");
        Check(Get<TextBox>(local, "_endpoint").Visible && Get<NumericUpDown>(local, "_timeout").Visible &&
            Get<Label>(local, "_message").Text.StartsWith("Settings saved"), "fixed Local editor finishes rebuilding and keeps inputs visible after Save");
        Click(local, "save");
        UiUntil(() => !Get<bool>(local, "_busy"));
        Check(Get<TextBox>(local, "_endpoint").Visible, "repeated Local Save remains usable without an attached nested type selector");
        using (var image = new Bitmap(form.ClientSize.Width, form.ClientSize.Height))
        {
            form.DrawToBitmap(image, form.ClientRectangle);
            image.Save(Path.Combine(evidence, "direct-local-settings.png"));
        }
        connection.SelectedIndex = 0;
        Click(view, "save");
        UiUntil(() => !Get<bool>(view, "_busy"));
        Check(service.ReadSettings().LocalHost == "192.0.2.123" && service.ReadSettings().LocalProfile is not null,
            "primary Cloud Save merges newest alternative settings rather than overwriting cached legacy data");
        connection.SelectedIndex = 1;
        Pump(100);
        Check(alternatives[1] != legacy, "local editor refreshes when saved backend settings change");
        connection.SelectedIndex = 0;
        LoginDialog(evidence);
        SaveLayoutStability(view, connection, evidence);
        FireHistoryUi(root, evidence, service, handler);
        form.ClientSize = new Size(720, 600);
        Pump(100);
        Check(Get<TableLayoutPanel>(view, "_columns").ColumnCount == 1, "narrow viewport reflows to one column");
        view.SetPageActive(true);
        Pump(100);
        view.SetPageActive(false);
        Check(!Get<System.Windows.Forms.Timer>(view, "_timer").Enabled, "hidden settings timer stops without stopping background service");
        foreach (var actor in new[] { new UserSession { UserId = "reader", Username = "reader", RoleName = "Operator" }, new UserSession { RoleName = "Administrator" } })
        {
            using var restricted = new FireConnectionSettingsView(service, actor);
            Check(!Get<TextBox>(restricted, "_token").Enabled && !Get<NumericUpDown>(restricted, "_poll").Enabled, "role and authentication protect cloud settings");
        }
        form.Close();
    }

    private static void SaveLayoutStability(FireConnectionSettingsView view, ComboBox selector, string evidence)
    {
        foreach (var size in new[] { new Size(1300, 650), new Size(1620, 830) })
        {
            view.FindForm()!.ClientSize = size;
            foreach (var mode in new[] { 0, 1, 2, 3 })
            {
                selector.SelectedIndex = Math.Min(mode, 2);
                Control editor = mode == 0 ? view : Get<Dictionary<int, LegacyFireConnectionSettingsView>>(view, "_alternatives")[Math.Min(mode, 2)];
                if (mode >= 2)
                    Get<ComboBox>(editor, "_bmsType").SelectedIndex = mode == 3 ? 0 : 1;
                Pump(300);
                var viewport = Get<Panel>(editor, "_scroll");
                var controls = Descendants(viewport).Where(control => control.Visible &&
                    (control is Label || control is TextBox || control is NumericUpDown || control is ComboBox || control is CheckBox)).ToArray();
                var before = controls.ToDictionary(control => control, control =>
                    (Bounds: new Rectangle(view.PointToClient(control.PointToScreen(Point.Empty)), control.Size),
                     Font: (control.Font.Name, control.Font.SizeInPoints, control.Font.Style),
                     Maximum: control.MaximumSize));
                using (var initial = new Bitmap(view.Width, view.Height))
                {
                    view.DrawToBitmap(initial, view.ClientRectangle);
                    initial.Save(Path.Combine(evidence, $"before-save-mode-{mode}-{size.Width}x{size.Height}.png"));
                }
                for (var iteration = 0; iteration < 3; iteration++)
                {
                    Click(editor, "save");
                    UiUntil(() => !Get<bool>(editor, "_busy"));
                    Check(Get<Label>(editor, "_message").Text.StartsWith("Settings saved", StringComparison.Ordinal),
                        $"mode {mode} Save actually succeeds before layout is compared");
                    foreach (var control in controls)
                    {
                        Check(!control.IsDisposed && control.Visible, $"Save keeps the same visible {control.GetType().Name} '{control.Text}' in mode {mode}");
                        var current = (Bounds: new Rectangle(view.PointToClient(control.PointToScreen(Point.Empty)), control.Size),
                            Font: (control.Font.Name, control.Font.SizeInPoints, control.Font.Style), Maximum: control.MaximumSize);
                        Check(current == before[control], $"Save preserves geometry/font/wrap for '{control.Text}' mode {mode}, iteration {iteration}, size {size}: {before[control]} -> {current}");
                    }
                    Pump(300);
                    foreach (var control in controls)
                    {
                        var current = (Bounds: new Rectangle(view.PointToClient(control.PointToScreen(Point.Empty)), control.Size),
                            Font: (control.Font.Name, control.Font.SizeInPoints, control.Font.Style), Maximum: control.MaximumSize);
                        Check(current == before[control],
                            $"settled Save preserves geometry/font/wrap for '{control.Text}' mode {mode}, iteration {iteration}, size {size}: {before[control]} -> {current}");
                    }
                }
                using var capture = new Bitmap(view.Width, view.Height);
                view.DrawToBitmap(capture, view.ClientRectangle);
                capture.Save(Path.Combine(evidence, $"save-stable-mode-{mode}-{size.Width}x{size.Height}.png"));
            }
        }
        selector.SelectedIndex = 0;
        Click(view, "save");
        UiUntil(() => !Get<bool>(view, "_busy"));
    }

    private static void FireHistoryUi(string root, string evidence, FireConnectionService service, Handler handler)
    {
        using var manager = new FirePanelManager(service);
        var settings = new RecordingSettings { ExportFolderPath = root };
        settings.Normalize();
        using var historian = new MeasurementHistorian(Path.Combine(root, "history-fixture.db"), recordingSettings: settings, migrateLegacyDatabase: false);
        using var scheduler = new DailyHistorianExportScheduler(historian, settings, root);
        using var history = new HistorianView(settings, Admin, historian, scheduler, _ => Task.CompletedTask, firePanels: manager) { Dock = DockStyle.Fill };
        using var form = new Form { StartPosition = FormStartPosition.Manual, Location = Point.Empty };
        form.Controls.Add(history);
        form.Show();
        var tabs = Get<FlatTabControl>(history, "_tabs");
        Check(tabs.TabPages.Cast<TabPage>().Select(page => page.Text).SequenceEqual(
            new[] { "Acquisition", "Raw recording", "Daily reports", "Active events", "Equipment history", "Fire Events" }),
            "Historian adds Fire Events without replacing or reordering existing pages");
        Check(tabs.SelectedIndex == 4, "equipment history remains default historian page");
        var events = Get<FireEventsView>(history, "_fireEvents");
        Check(Get<ComboBox>(events, "_filter").Items.Contains("Service"), "observed service category is filterable without reclassifying it as a live system state");
        var before = handler.Requests;
        tabs.SelectedIndex = 5;
        SynchronizationContext.SetSynchronizationContext(uiContext);
        history.SetPageActive(true);
        UiUntil(() => !Get<bool>(events, "_loading"));
        Check(ReferenceEquals(events.Parent, tabs.TabPages[5]) && Get<System.Windows.Forms.Timer>(events, "_timer").Enabled,
            "Fire Events is a fill page owned and activated by Historian");
        Check(handler.Requests == before, "opening historian fire history does not initiate a network request");
        Click(events, "sync-events");
        UiUntil(() => Get<CancellationTokenSource?>(events, "_cancellation") is null);
        var grid = Get<DataGridView>(events, "_grid");
        Check(grid.Rows.Count == 1 && grid.Rows[0].Cells[7].Value?.ToString() == "fixture historical event", "historian shows the same persistent event history from the existing shared service");
        Get<ComboBox>(events, "_filter").SelectedItem = "Fault";
        Check(grid.Rows.Count == 0, "historian type filter does not relabel events");
        Get<ComboBox>(events, "_filter").SelectedIndex = 0;
        Get<TextBox>(events, "_search").Text = "not in fixture";
        Check(grid.Rows.Count == 0, "historian fire search filters displayed records");
        Get<TextBox>(events, "_search").Clear();
        foreach (var size in new[] { new Size(1080, 520), new Size(1300, 650), new Size(1620, 830) })
        {
            form.ClientSize = size;
            Pump(100);
            Check(grid.Height >= 250 && grid.Width >= 900, $"Fire Events uses available historian height and width at {size}");
            Check(grid.PointToScreen(new Point(0, grid.Height)).Y <= form.PointToScreen(new Point(0, form.ClientSize.Height)).Y,
                $"last table border fully visible without nested scroll at {size}");
            Check(grid.ScrollBars == ScrollBars.Both && grid.Columns[0].Frozen, "multi-column history keeps timestamp frozen and horizontal scroll");
            using var image = new Bitmap(size.Width, size.Height);
            form.DrawToBitmap(image, form.ClientRectangle);
            image.Save(Path.Combine(evidence, $"historian-fire-events-{size.Width}x{size.Height}.png"));
        }
        var second = manager.AddPanel("second fixture panel", Admin);
        UiUntil(() => !Get<bool>(events, "_loading"));
        var selector = Get<ComboBox>(events, "_panels");
        Check(selector.Items.Count == 2, "historian panel list follows shared manager additions");
        selector.SelectedItem = second;
        Click(events, "refresh-events");
        UiUntil(() => !Get<bool>(events, "_loading"));
        Check(grid.Rows.Count == 0 && !Get<Button>(events, "_sync").Enabled, "different local-only panel does not inherit first panel history or allow cloud sync");
        selector.SelectedIndex = 0;
        Click(events, "refresh-events");
        UiUntil(() => !Get<bool>(events, "_loading"));
        Check(grid.Rows.Count == 1, "switching back restores original panel history");
        tabs.SelectedIndex = 4;
        Check(!Get<System.Windows.Forms.Timer>(events, "_timer").Enabled, "leaving historian Fire Events stops its display timer");
        history.SetPageActive(false);
        var actor = new UserSession { UserId = "reader", Username = "reader", RoleName = "Operator" };
        using var readOnly = new FireEventsView(manager, actor);
        Check(!Get<Button>(readOnly, "_sync").Enabled, "viewer can browse history but cannot send authorized event requests");
        form.Close();
    }

    private static T Get<T>(object instance, string name) => (T)instance.GetType().GetField(name, Fields)!.GetValue(instance)!;
    private static void Pump(int milliseconds)
    {
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < milliseconds) { Application.DoEvents(); Thread.Sleep(5); }
    }
    private static void UiUntil(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition() && watch.ElapsedMilliseconds < 4000) Pump(10);
        Check(condition(), "UI fixture action completed");
    }
    private static void Click(Control root, string name)
    {
        SynchronizationContext.SetSynchronizationContext(uiContext);
        var button = Descendants(root).OfType<Button>().Single(item => item.Name == name);
        Check(button.Enabled, name + " enabled for authenticated administrator");
        // PerformClick ignores controls scrolled out of view, while OnClick exercises the same real handler.
        typeof(Button).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(button, new object[] { EventArgs.Empty });
    }
    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        {
            if (child is LegacyFireConnectionSettingsView) continue;
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
    private static async Task Until(Func<bool> condition, int timeoutMilliseconds = 4000)
    {
        var watch = Stopwatch.StartNew();
        while (!condition() && watch.ElapsedMilliseconds < timeoutMilliseconds) await Task.Delay(10);
        Check(condition(), "asynchronous fixture reached expected state");
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        assertions++;
    }
    private static async Task Throws<T>(Func<Task> work, string message) where T : Exception
    {
        try { await work(); }
        catch (T) { assertions++; return; }
        throw new InvalidOperationException(message);
    }

    private sealed class Handler : HttpMessageHandler
    {
        internal const string PanelToken = "fixture-only-short-lived-panel-token";
        internal const string LoginToken = "fixture-only-login-session-token";
        internal HttpStatusCode LoginStatus = HttpStatusCode.OK;
        internal string? LoginBody;
        internal string LastLoginBody = "", LastLoginAuthorization = "", LastLoginUri = "";
        internal HttpMethod? LastLoginMethod;
        internal int LoginRequests;
        internal int RejectAuthRequests, RejectEventRequests;
        internal HttpStatusCode RejectedAuthStatus = HttpStatusCode.Unauthorized;
        internal HttpStatusCode RejectedEventStatus = HttpStatusCode.Unauthorized;
        internal string? AuthBody;
        internal HttpStatusCode AuthStatus = HttpStatusCode.OK;
        internal int AuthRequests, EventRequests;
        internal string LastAuthAuthorization = "", LastAuthBody = "";
        internal string? Challenge;
        internal string Body = EventJson;
        internal string ContentType = "application/json";
        internal HttpStatusCode Status = HttpStatusCode.OK;
        internal int? RetryAfter;
        internal int Delay;
        internal Exception? Failure;
        internal string LastUri = "", LastAuthorization = "";
        internal HttpMethod? LastMethod;
        internal int Requests, Active, MaximumActive;
        internal readonly System.Collections.Concurrent.ConcurrentQueue<long> Starts = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Requests);
            var active = Interlocked.Increment(ref Active);
            MaximumActive = Math.Max(MaximumActive, active);
            try
            {
                LastUri = request.RequestUri!.AbsoluteUri;
                LastAuthorization = request.Headers.Authorization?.ToString() ?? "";
                LastMethod = request.Method;
                if (Delay > 0) await Task.Delay(Delay, cancellationToken);
                if (Failure is not null) throw Failure;
                if (request.RequestUri.AbsolutePath == ZetaCloudEventClient.LoginRoute)
                {
                    Interlocked.Increment(ref LoginRequests);
                    LastLoginBody = await request.Content!.ReadAsStringAsync(cancellationToken);
                    LastLoginAuthorization = LastAuthorization;
                    LastLoginUri = LastUri;
                    LastLoginMethod = request.Method;
                    var loginResponse = new HttpResponseMessage(LoginStatus)
                    {
                        Content = new StringContent(LoginBody ?? JsonSerializer.Serialize(new { token = LoginToken }), Encoding.UTF8, ContentType)
                    };
                    if (RetryAfter.HasValue) loginResponse.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(RetryAfter.Value));
                    return loginResponse;
                }
                if (request.RequestUri.AbsolutePath == ZetaCloudEventClient.AuthorizationRoute)
                {
                    if (request.Method != HttpMethod.Post) throw new InvalidOperationException("Authorization must use POST");
                    Interlocked.Increment(ref AuthRequests);
                    LastAuthAuthorization = LastAuthorization;
                    LastAuthBody = await request.Content!.ReadAsStringAsync(cancellationToken);
                    var authStatus = AuthStatus;
                    if (RejectAuthRequests > 0) { RejectAuthRequests--; authStatus = RejectedAuthStatus; }
                    var authResponse = new HttpResponseMessage(authStatus)
                    {
                        Content = new StringContent(AuthBody ??
                        JsonSerializer.Serialize(new { token = PanelToken, expires = DateTimeOffset.UtcNow.AddMinutes(15).ToUnixTimeSeconds() }), Encoding.UTF8, ContentType)
                    };
                    if (RetryAfter.HasValue) authResponse.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(RetryAfter.Value));
                    if (Challenge is not null) authResponse.Headers.WwwAuthenticate.Add(new AuthenticationHeaderValue("Bearer", Challenge));
                    return authResponse;
                }
                Interlocked.Increment(ref EventRequests);
                Starts.Enqueue(Stopwatch.GetTimestamp());
                var eventStatus = Status;
                if (RejectEventRequests > 0) { RejectEventRequests--; eventStatus = RejectedEventStatus; }
                var response = new HttpResponseMessage(eventStatus) { Content = new StringContent(Body, Encoding.UTF8, ContentType) };
                if (RetryAfter.HasValue) response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(RetryAfter.Value));
                if (Challenge is not null) response.Headers.WwwAuthenticate.Add(new AuthenticationHeaderValue("Bearer", Challenge));
                return response;
            }
            finally { Interlocked.Decrement(ref Active); }
        }
    }
}
