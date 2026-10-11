# GPU worker image (RunPod / Modal / any CUDA host). Built at deploy time (build step 13):
#   docker build -t gs-worker worker/
#   docker run --gpus all -e GS_API_URL=https://api.example.com -e GS_WORKER_TOKEN=... gs-worker
#
# gsplat's CUDA kernels are compiled during the image build (not on first job), so jobs start fast.
# A GPU is not needed to build: TORCH_CUDA_ARCH_LIST tells nvcc which GPUs to target
# (7.5 T4, 8.0 A100, 8.6 A10/3090, 8.9 L4/4090, 9.0 H100).

FROM pytorch/pytorch:2.7.0-cuda12.6-cudnn9-devel

ENV PYTHONUNBUFFERED=1 \
    TORCH_CUDA_ARCH_LIST="7.5;8.0;8.6;8.9;9.0" \
    MAX_JOBS=4

RUN pip install --no-cache-dir ninja jaxtyping rich scipy httpx jsonschema pillow numpy \
    pycolmap opencv-python-headless \
 && pip install --no-cache-dir --no-deps gsplat==1.5.3 \
 && python -c "from gsplat.cuda._backend import _C; assert _C is not None, 'gsplat CUDA build failed'"

WORKDIR /app
COPY pyproject.toml ./
COPY gs_pipeline ./gs_pipeline
COPY gs_worker ./gs_worker
RUN pip install --no-cache-dir --no-deps -e .

CMD ["python", "-m", "gs_worker"]
