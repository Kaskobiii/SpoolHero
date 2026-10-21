#!/usr/bin/env python3
"""API regression for favorites spanning material pages.

Run against a disposable SpoolHero instance:
    SPOOLHERO_TEST_EMAIL=admin@localhost SPOOLHERO_TEST_PASSWORD=admin \
        python3 tests/material_favorites_smoke.py
An existing JWT can instead be supplied as SPOOLHERO_TEST_TOKEN.
Set SPOOLHERO_TEST_URL to override http://127.0.0.1:5087.
The test creates two uniquely named projects and retains their materials for
inspection. It removes its favorite selections in the finally block. Use a
disposable instance: existing project-member foreign keys prevent project cleanup
through the API while its final admin membership still exists.
"""

import json
import os
import urllib.error
import urllib.parse
import urllib.request
import uuid


BASE = os.environ.get("SPOOLHERO_TEST_URL", "http://127.0.0.1:5087").rstrip("/")
TOKEN = os.environ.get("SPOOLHERO_TEST_TOKEN")


def request(method, path, body=None, project=None):
    headers = {"Authorization": f"Bearer {TOKEN}"} if TOKEN else {}
    if project:
        headers["X-Project-Id"] = project
    data = None
    if body is not None:
        headers["Content-Type"] = "application/json"
        data = json.dumps(body).encode()
    req = urllib.request.Request(BASE + path, data, headers, method=method)
    try:
        with urllib.request.urlopen(req, timeout=30) as response:
            payload = response.read()
            return json.loads(payload) if payload else None
    except urllib.error.HTTPError as error:
        raise AssertionError(f"{method} {path}: {error.code} {error.read().decode()}") from error


def main():
    global TOKEN
    if not TOKEN:
        TOKEN = request("POST", "/api/auth/login", {
            "email": os.environ["SPOOLHERO_TEST_EMAIL"],
            "password": os.environ["SPOOLHERO_TEST_PASSWORD"],
        })["accessToken"]
    run = uuid.uuid4().hex[:12]
    brand_prefix = f"Favorites regression {run}"
    projects = []
    favorites = set()
    materials = []
    try:
        for suffix in ("main", "other"):
            project = request("POST", "/api/projects", {"name": f"{brand_prefix} {suffix}"})
            projects.append(project["id"])
        project_id, other_project_id = projects

        def create_material(index, project):
            return request("POST", "/api/materials", {
                "brand": f"{brand_prefix} {index:03}",
                "type": "PETG" if index == 75 else "PLA",
                "colorName": "Blue" if index in (50, 75) else "Red",
                "colorHex": "0000FF" if index in (50, 75) else "FF0000",
                "minTempCelsius": 200,
                "maxTempCelsius": 230,
                "diameterMm": 1.75,
            }, project)["id"]

        for index in range(101):
            materials.append(create_material(index, project_id))
        other_material = create_material(102, other_project_id)

        def page(number=0, size=2, only_favorites=True, **filters):
            query = {
                "page": number,
                "pageSize": size,
                "brand": brand_prefix,
                "sortBy": "brand",
                "sortAsc": "true",
                "favoritesOnly": str(only_favorites).lower(),
                **filters,
            }
            return request("GET", "/api/materials/paged?" + urllib.parse.urlencode(query), project=project_id)

        assert page()["totalCount"] == 0, "No materials should be favorites initially"
        selected = [materials[index] for index in (0, 49, 50, 75, 100)]
        for material_id in selected + [other_material]:
            request("POST", f"/api/favorites/{material_id}")
            favorites.add(material_id)

        # Three selected favorites lie beyond the original unfiltered 50-item page.
        original_page = page(size=50, only_favorites=False)
        assert original_page["totalCount"] == 101
        assert not set(selected[2:]) & {item["id"] for item in original_page["items"]}

        found = []
        for number, expected_size in enumerate((2, 2, 1)):
            result = page(number)
            assert result["totalCount"] == 5, "Favorite count must precede pagination and respect project scope"
            assert len(result["items"]) == expected_size
            found.extend(item["id"] for item in result["items"])
        assert found == selected, "All favorites must appear exactly once in the requested order"
        result = page(type="PLA", color="Blue")
        assert result["totalCount"] == 1 and result["items"][0]["id"] == materials[50]
        assert page(type="ABS")["totalCount"] == 0

        request("DELETE", f"/api/favorites/{selected[-1]}")
        favorites.remove(selected[-1])
        assert page(2)["totalCount"] == 4 and page(2)["items"] == []
        assert len(page(1)["items"]) == 2, "Previous page must be available after removing the final favorite"
        print("PASS: favorites beyond 50 materials; correct totals, pagination, combined filters, project isolation, and removal")
    finally:
        for material_id in favorites:
            request("DELETE", f"/api/favorites/{material_id}")
        if projects:
            print("Retained inspection projects: " + ", ".join(projects))


if __name__ == "__main__":
    main()
