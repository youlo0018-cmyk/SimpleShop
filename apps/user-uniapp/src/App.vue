<script setup lang="ts">
import { onLaunch } from '@dcloudio/uni-app';
import { useSessionStore } from '@/stores/session';

onLaunch(() => {
  const session = useSessionStore();
  session.restore();
});
</script>

<style>
page {
  background: #f5f5f7;
  color: #111;
  font-family: 'SimpleShop Sans', -apple-system, BlinkMacSystemFont, 'PingFang SC', sans-serif;
}
</style>
