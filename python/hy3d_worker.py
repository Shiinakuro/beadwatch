"""
拼豆先锋 · 本地 AI 工作进程（混元3D 形状生成 + 主体抠图）

由 C# 主程序以子进程方式启动，标准输入/输出逐行传 JSON（日志写 stderr）。
模型都是用到时才加载：只抠图时不会占用显存。

  启动后发送   {"event": "ready"}
  形状请求     {"id": 1, "cmd": "shape", "views": {"front": "f.png", ...}, "out": "shape.obj", "steps": 5, "octree": 256, "seed": 1234}
  抠图请求     {"id": 2, "cmd": "cutout", "in": "photo.jpg", "out": "cut.png", "box": [x0, y0, x1, y1]}
               box 可省略；给出时先裁剪到框内再抠图，输出图尺寸 = 框尺寸
  进度         {"id": 1, "event": "progress", "stage": "..."}
  结果         {"id": 1, "event": "done", ...}
  失败         {"id": 1, "event": "error", "error": "..."}
  退出         {"quit": true}

视图约定（与混元 2mv 相同）：front=正面；left=从物体左侧看；back=背面；right=从物体右侧看。
"""
import json
import os
import sys
import time
import traceback

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
sys.path.insert(0, os.path.join(ROOT, "runtime"))           # runtime/hy3dgen
os.environ.setdefault("HY3DGEN_MODELS", os.path.join(ROOT, "models"))
os.environ.setdefault("U2NET_HOME", os.path.join(ROOT, "models", "u2net"))
os.environ.setdefault("HF_HUB_OFFLINE", "1")
# 主程序按 UTF-8 收发；Windows 控制台默认 GBK，必须显式改掉
sys.stdout.reconfigure(encoding="utf-8")
sys.stdin.reconfigure(encoding="utf-8")


def emit(obj):
    sys.stdout.write(json.dumps(obj, ensure_ascii=False) + "\n")
    sys.stdout.flush()


def log(*a):
    print(*a, file=sys.stderr, flush=True)


class Shape:
    """混元3D-2mv，首次使用时加载。"""

    def __init__(self):
        self.pipe = None

    def load(self, rid):
        if self.pipe is not None:
            return
        import torch
        if not torch.cuda.is_available():
            raise RuntimeError("未检测到可用的 NVIDIA 显卡 (CUDA)")
        emit({"id": rid, "event": "progress", "stage": "加载混元3D 模型（首次约 30 秒）"})
        from hy3dgen.shapegen import Hunyuan3DDiTFlowMatchingPipeline, FloaterRemover, DegenerateFaceRemover
        self.pipe = Hunyuan3DDiTFlowMatchingPipeline.from_pretrained(
            "tencent/Hunyuan3D-2mv", subfolder="hunyuan3d-dit-v2-mv-turbo", variant="fp16", use_safetensors=True)
        try:
            self.pipe.enable_flashvdm()
        except Exception as e:
            log("flashvdm disabled:", e)
        self.rm_float, self.rm_degen = FloaterRemover(), DegenerateFaceRemover()
        self.device = torch.cuda.get_device_name(0)

    def run(self, rid, req):
        import torch
        from PIL import Image
        self.load(rid)
        views = {}
        for k, p in req["views"].items():
            if k in ("front", "left", "back", "right") and p and os.path.isfile(p):
                views[k] = Image.open(p).convert("RGBA")
        if "front" not in views:
            raise ValueError("缺少正面视图")
        emit({"id": rid, "event": "progress", "stage": "扩散生成形状 (%s)" % ",".join(views)})
        t0 = time.time()
        gen = torch.manual_seed(int(req.get("seed", 1234)))
        mesh = self.pipe(image=views, num_inference_steps=int(req.get("steps", 5)),
                         octree_resolution=int(req.get("octree", 256)), guidance_scale=float(req.get("guidance", 5.0)),
                         num_chunks=20000, generator=gen, output_type="trimesh")[0]
        emit({"id": rid, "event": "progress", "stage": "清理网格"})
        mesh = self.rm_degen(self.rm_float(mesh))
        out = req["out"]
        os.makedirs(os.path.dirname(os.path.abspath(out)), exist_ok=True)
        mesh.export(out)
        torch.cuda.empty_cache()
        return {"out": out, "seconds": round(time.time() - t0, 2), "vertices": int(len(mesh.vertices)),
                "faces": int(len(mesh.faces)), "device": self.device}


class Cutout:
    """主体抠图：ISNet（rembg），CPU 即可，约 1 秒/张。"""

    def __init__(self):
        self.session = None

    def run(self, rid, req):
        from PIL import Image, ImageOps
        if self.session is None:
            emit({"id": rid, "event": "progress", "stage": "加载抠图模型"})
            from rembg import new_session
            self.session = new_session("isnet-general-use")
        from rembg import remove
        # 与主程序一致：先按 EXIF 方向转正，框选坐标才对得上
        im = ImageOps.exif_transpose(Image.open(req["in"]))
        im = im.convert("RGBA") if im.mode in ("RGBA", "LA", "P") else im.convert("RGB")
        box = req.get("box")
        if box:
            x0, y0, x1, y1 = [int(round(v)) for v in box]
            x0, y0 = max(0, x0), max(0, y0)
            x1, y1 = min(im.width, x1), min(im.height, y1)
            if x1 - x0 > 8 and y1 - y0 > 8:
                im = im.crop((x0, y0, x1, y1))
        out = remove(im.convert("RGB"), session=self.session, post_process_mask=True)
        os.makedirs(os.path.dirname(os.path.abspath(req["out"])), exist_ok=True)
        out.save(req["out"])
        return {"out": req["out"], "width": out.width, "height": out.height}


def main():
    shape, cut = Shape(), Cutout()
    emit({"event": "ready"})
    for line in sys.stdin:
        line = line.strip()
        if not line:
            continue
        rid = None
        try:
            req = json.loads(line)
            if req.get("quit"):
                break
            rid = req.get("id")
            cmd = req.get("cmd", "shape")
            result = cut.run(rid, req) if cmd == "cutout" else shape.run(rid, req)
            result.update({"id": rid, "event": "done"})
            emit(result)
        except Exception as e:
            log(traceback.format_exc())
            emit({"id": rid, "event": "error", "error": str(e)})
    return 0


if __name__ == "__main__":
    sys.exit(main() or 0)
