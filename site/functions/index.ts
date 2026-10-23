// Legacy Qoder Sites function. GitHub Pages uses tools/prepare_pages.py and a static installer.json.
// 拼豆先锋安装包分发：固定文件的分片签名读取，不接受客户端传入路径。
import { storage } from "./_qoder/storage.mjs";

type Part = { path: string; size: number };

const INSTALLER_PARTS: Part[] = [
  { path: "download/beadwatch-1.0/beadwatch-1.0.part01", size: 9437184 },
  { path: "download/beadwatch-1.0/beadwatch-1.0.part02", size: 9437184 },
  { path: "download/beadwatch-1.0/beadwatch-1.0.part03", size: 9437184 },
  { path: "download/beadwatch-1.0/beadwatch-1.0.part04", size: 9437184 },
  { path: "download/beadwatch-1.0/beadwatch-1.0.part05", size: 9437184 },
  { path: "download/beadwatch-1.0/beadwatch-1.0.part06", size: 9437184 },
  { path: "download/beadwatch-1.0/beadwatch-1.0.part07", size: 9437184 },
  { path: "download/beadwatch-1.0/beadwatch-1.0.part08", size: 9437184 },
  { path: "download/beadwatch-1.0/beadwatch-1.0.part09", size: 9437184 },
  { path: "download/beadwatch-1.0/beadwatch-1.0.part10", size: 9437184 },
  { path: "download/beadwatch-1.0/beadwatch-1.0.part11", size: 9437184 },
  { path: "download/beadwatch-1.0/beadwatch-1.0.part12", size: 3813398 },
];

const INSTALLER_META = {
  name: "拼豆先锋_v1.0_安装程序.exe",
  version: "1.0",
  size: 107622422,
  sha256: "2f4e53bda1a0deca3399c712dac8ec0bff4e057b89d2206bd1befafd16a82a55",
};

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: {
      "content-type": "application/json; charset=utf-8",
      "cache-control": "no-store",
      "x-content-type-options": "nosniff",
    },
  });
}

async function sign(parts: Part[]) {
  return await Promise.all(parts.map(async (p) => {
    const { signedUrl } = await storage.createSignedUrl(p.path, 3600);
    return { url: signedUrl, size: p.size };
  }));
}

Deno.serve(async (request: Request) => {
  if (request.method !== "GET") {
    return new Response(JSON.stringify({ ok: false, error: "method_not_allowed" }), {
      status: 405,
      headers: {
        "content-type": "application/json; charset=utf-8",
        "allow": "GET",
        "cache-control": "no-store",
      },
    });
  }
  const path = new URL(request.url).pathname;
  try {
    if (path.endsWith("/download/installer")) {
      return json({ ok: true, ...INSTALLER_META, parts: await sign(INSTALLER_PARTS) });
    }
    return json({ ok: false, error: "not_found" }, 404);
  } catch (error) {
    console.error("installer sign failed:", error instanceof Error ? error.message : "unknown");
    return json({ ok: false, error: "service_unavailable" }, 503);
  }
});
