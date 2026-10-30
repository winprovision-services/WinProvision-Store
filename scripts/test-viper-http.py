"""Exercise the shipped Viper CLI over real local HTTP; no external service required."""
import hashlib
import http.server
import json
import pathlib
import subprocess
import sys
import threading
import time


def main():
    executable = pathlib.Path(sys.argv[1]).resolve()
    root = pathlib.Path(__file__).resolve().parent.parent / "artifacts" / "viper" / "http-validation"
    # Each run owns its outputs and never deletes downloads anteriores.
    output = root / time.strftime("%Y%m%d-%H%M%S")
    output.mkdir(parents=True, exist_ok=False)
    payload = bytes(range(256)) * (16 * 1024 * 1024 // 256)
    expected = hashlib.sha256(payload).hexdigest()

    class Handler(http.server.BaseHTTPRequestHandler):
        protocol_version = "HTTP/1.1"

        def log_message(self, *_):
            pass

        def do_GET(self):
            start, end = 0, len(payload) - 1
            partial = self.headers.get("Range")
            if partial:
                start, end = map(int, partial.removeprefix("bytes=").split("-"))
            self.send_response(206 if partial else 200)
            self.send_header("Content-Length", str(end - start + 1))
            self.send_header("ETag", '"viper-native-test-v1"')
            self.send_header("Connection", "close")
            if partial:
                self.send_header("Content-Range", f"bytes {start}-{end}/{len(payload)}")
            self.end_headers()
            try:
                for offset in range(start, end + 1, 32768):
                    self.wfile.write(payload[offset:min(end + 1, offset + 32768)])
                    self.wfile.flush()
                    time.sleep(0.002)  # Controlled bandwidth per connection, not an Internet benchmark.
            except (BrokenPipeError, ConnectionResetError):
                pass

    server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Handler)
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    url = f"http://127.0.0.1:{server.server_port}/payload"
    common = [str(executable), "download", url, "--allow-http", "--piece-mib", "1", "--sha256", expected, "--json"]
    results = {}
    try:
        for name, connections in [("single", 1), ("segmented", 4)]:
            target = output / f"{name}.bin"
            started = time.perf_counter()
            process = subprocess.run(common + ["--connections", str(connections), "--output", str(target)], capture_output=True, text=True, timeout=30)
            elapsed = time.perf_counter() - started
            assert process.returncode == 0, process.stderr
            parsed = json.loads(process.stdout)[0]
            assert parsed["sha256"] == expected and parsed["bytes"] == len(payload)
            assert hashlib.sha256(target.read_bytes()).hexdigest() == expected
            results[name] = {"seconds": elapsed, "bytes": len(payload), "sha256": expected, "segmented": parsed["segmented"]}
        assert not results["single"]["segmented"] and results["segmented"]["segmented"]
        existing = output / "single.bin"
        denied = subprocess.run(common + ["--output", str(existing)], capture_output=True, text=True, timeout=10)
        assert denied.returncode == 1 and not denied.stdout
        assert hashlib.sha256(existing.read_bytes()).hexdigest() == expected
        jobs = [{"url": url, "destination": str(output / f"batch-{i}.bin"), "sha256": expected} for i in range(3)]
        batch_file = output / "jobs.json"
        batch_file.write_text(json.dumps(jobs), encoding="utf-8")
        batch = subprocess.run([str(executable), "batch", str(batch_file), "--allow-http", "--parallel", "2", "--connections", "2", "--piece-mib", "1", "--json"], capture_output=True, text=True, timeout=30)
        assert batch.returncode == 0, batch.stderr
        assert len(json.loads(batch.stdout)) == 3
        assert all(hashlib.sha256(pathlib.Path(job["destination"]).read_bytes()).hexdigest() == expected for job in jobs)
        results["native_cli"] = {"batch_files": 3, "existing_file_preserved": True, "json_validated": True}
        results["controlled_speed_ratio"] = results["single"]["seconds"] / results["segmented"]["seconds"]
        results["scope"] = "Local HTTP with artificial per-connection throttling; not an Internet or aria2 comparison."
        (output / "result.json").write_text(json.dumps(results, indent=2), encoding="utf-8")
        print(json.dumps(results, indent=2))
        print(f"Evidence: {output}")
    finally:
        server.shutdown()
        server.server_close()
        thread.join()


if __name__ == "__main__":
    main()
