"""FastAPI app. Run locally:  uvicorn app.main:app --host 0.0.0.0 --port 8000  (from backend/)."""

import mimetypes

from fastapi import FastAPI
from fastapi.staticfiles import StaticFiles

from .config import Settings
from .db import make_session_factory
from .routers import auth, captures, files, worker
from .storage import make_storage


def create_app(settings: Settings | None = None) -> FastAPI:
    settings = settings or Settings()
    app = FastAPI(title="3DGS capture backend", version="0.1.0")
    app.state.settings = settings
    app.state.session_factory = make_session_factory(settings.database_url)
    app.state.storage = make_storage(settings)
    for r in (auth, captures, worker, files):
        app.include_router(r)

    # The splat viewer (repo/viewer) is a self-contained static add-on; serving it here keeps it on the same
    # origin as the signed /files URLs it loads, so no CORS setup is needed.
    if settings.serve_viewer and settings.viewer_dir.is_dir():
        mimetypes.add_type("text/javascript", ".js")  # Windows registries sometimes map .js to text/plain
        app.mount("/viewer", StaticFiles(directory=settings.viewer_dir, html=True), name="viewer")

    @app.get("/health")
    def health():
        return {"ok": True, "storage": settings.storage_backend}

    return app


app = create_app()
