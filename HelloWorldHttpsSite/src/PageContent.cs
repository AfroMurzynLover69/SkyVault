public static class PageContent
{
    public static string GetHtml()
    {
        return """
        <!doctype html>
        <html lang="en">
        <head>
          <meta charset="utf-8">
          <meta name="viewport" content="width=device-width, initial-scale=1">
          <title>Hello World</title>
          <style>
            * {
              box-sizing: border-box;
            }

            body {
              margin: 0;
              min-height: 100vh;
              display: grid;
              place-items: center;
              font-family: Arial, sans-serif;
              background: #f4f7fb;
              color: #111827;
            }

            main {
              text-align: center;
              padding: 32px;
            }

            h1 {
              margin: 0;
              font-size: 56px;
              line-height: 1.1;
            }
          </style>
        </head>
        <body>
          <main>
            <h1>Hello World</h1>
          </main>
        </body>
        </html>
        """;
    }
}
