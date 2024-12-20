import http from "k6/http";
import { sleep, check } from "k6";

export const options = {
  vus: 20, // Number of virtual users to simulate
  iterations: 100, // Total number of script iterations across all VUs
};

const binFile = open("./src.zip", "b");

export default function () {
  const url = "https://redis-job-runner.test.aelf.dev/build"; // Replace with your endpoint

  // Create the payload for multipart/form-data request
  const payload = {
    file: http.file(binFile, "src.zip"),
  };

  // Send the POST request with the multipart/form-data payload
  const res = http.post(url, payload, { timeout: "120s" });

  // Validate response
  check(res, {
    "status is 200": (r) => r.status === 200,
  });

  sleep(6);
}
