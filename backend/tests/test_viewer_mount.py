def test_viewer_is_served_with_module_mime_types(client):
    r = client.get("/viewer/")
    assert r.status_code == 200 and "SplatSpace" in r.text
    js = client.get("/viewer/src/main.js")
    assert js.status_code == 200 and "javascript" in js.headers["content-type"]
    spark = client.get("/viewer/vendor/spark/spark.module.js")
    assert spark.status_code == 200 and len(spark.content) > 1_000_000
    assert client.get("/viewer/vendor/three/three.core.js").status_code == 200


def test_viewer_can_be_switched_off(settings):
    from fastapi.testclient import TestClient

    from app.main import create_app

    settings.serve_viewer = False
    with TestClient(create_app(settings)) as c:
        assert c.get("/viewer/").status_code == 404
