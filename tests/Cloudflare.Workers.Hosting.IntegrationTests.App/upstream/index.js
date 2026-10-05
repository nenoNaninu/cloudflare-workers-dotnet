export default {
  async fetch(request) {
    const url = new URL(request.url);
    const body = await request.text();
    return Response.json({
      worker: "upstream",
      method: request.method,
      path: url.pathname,
      body,
    });
  },
};
