import logging

from .collectors import host
from .config import Config
from .models import MAX_SAMPLE_BYTES, Sample
from .outbox import Outbox
from .transport import Outcome, Transport

LOG = logging.getLogger(__name__)


def tick(config: Config, transport: Transport) -> int:
    outbox = Outbox(config.state_directory)
    with outbox.sender() as acquired:
        if not acquired:
            return 0

        collection_failed = False
        try:
            outbox.enqueue(Sample.host(host.collect(config, use_cache=True)))
        except (OSError, ValueError, IndexError):
            LOG.error("Host collection failed")
            collection_failed = True

        removed = outbox.prune(config.max_pending, config.max_age_seconds)
        if removed:
            LOG.warning("Expired or excess samples removed: %s", removed)

        pending = outbox.pending()
        delivery = pending[-1:] + pending[:-1][:config.send_limit - 1]
        for path in delivery:
            with path.open("rb") as stream:
                payload = stream.read(MAX_SAMPLE_BYTES + 1)
            if len(payload) > MAX_SAMPLE_BYTES:
                LOG.warning("Removing an oversized queued sample")
                path.unlink()
                continue
            result = transport.send(payload)
            if result is Outcome.RETRY:
                LOG.warning("Delivery did not complete. Samples remain queued")
                return 1
            if result is Outcome.REJECTED:
                LOG.warning("Receiver rejected sample %s", path.stem[21:])
            path.unlink()
        return int(collection_failed)
