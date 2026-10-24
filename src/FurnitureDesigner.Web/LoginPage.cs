namespace FurnitureDesigner.Web;

/// <summary>The password form shown when APP_PASSWORD is set.</summary>
internal static class LoginPage
{
    public static string Html(bool failed) => $$"""
        <!doctype html>
        <html lang="en">
        <head>
          <meta charset="utf-8">
          <meta name="viewport" content="width=device-width, initial-scale=1">
          <title>Furniture Designer - Log in</title>
          <style>
            body { margin: 0; min-height: 100vh; display: grid; place-items: center; background: #eceef1;
                   font: 14px system-ui, -apple-system, "Segoe UI", sans-serif; color: #222; }
            form { background: #fff; padding: 28px; border-radius: 8px; box-shadow: 0 2px 12px #0002; width: min(320px, 90vw); }
            h1 { font-size: 18px; margin: 0 0 16px; }
            input { width: 100%; box-sizing: border-box; padding: 8px; font: inherit; border: 1px solid #bbb; border-radius: 4px; }
            button { margin-top: 12px; width: 100%; padding: 8px; font: inherit; font-weight: 600; border: 0;
                     border-radius: 4px; background: #2f6fb5; color: #fff; cursor: pointer; }
            .error { color: #b3261e; margin: 0 0 10px; }
          </style>
        </head>
        <body>
          <form method="post" action="/login">
            <h1>Furniture Designer</h1>
            {{(failed ? "<p class=\"error\">Wrong password.</p>" : "")}}
            <input type="password" name="password" placeholder="Password" autofocus required>
            <button type="submit">Log in</button>
          </form>
        </body>
        </html>
        """;
}
