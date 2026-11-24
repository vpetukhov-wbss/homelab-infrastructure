"""Send a synthetic sample using the agent's production encoder and HTTP transport."""

from dataclasses import replace
from datetime import datetime
import os
import sys

from pulse_agent.models import Sample
from pulse_agent.transport import Outcome, Transport

values = {
    "bootId": "8efee3f4-a0e6-4517-a139-8abe1252ed19",
    "cpuTimesSeconds": dict.fromkeys(("user", "nice", "system", "idle", "iowait", "irq", "softirq", "steal"), 1),
    "memory": {"totalBytes": 4096, "availableBytes": 2048, "swapTotalBytes": 0, "swapFreeBytes": 0},
    "loadAverage": [0.25, 0.5, 0.75],
    "uptimeSeconds": 42,
    "filesystems": {},
    "network": {},
    "temperaturesCelsius": {},
}
sample = Sample.host(values)
if len(sys.argv) > 2:
    sample = replace(sample, observed_at=datetime.fromisoformat(sys.argv[2]))
payload = sample.encode()
transport = Transport(sys.argv[1], os.environ["PULSE_TEST_BEARER"], timeout=5)
if transport.send(payload) is not Outcome.ACK:
    raise SystemExit("The receiver did not acknowledge the agent sample.")
sys.stdout.buffer.write(payload)
