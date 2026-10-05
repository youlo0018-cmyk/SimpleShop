// 「一份配置，读出来改一改存回去」这类页面的配置。
// 地区地址、优惠优先级、积分规则都是同一个形状，所以共用一个 ConfigView +
// 各自的字段声明；不为此写三个组件。

export const CONFIGS = {
  // 地区地址。JSON 数组存 textarea，2 MB 上限所以要给字节数提示 ——
  // 用户必须能一眼看出离上限还有多远，只显示「2097152」没法判断。
  regions: {
    title: '地区地址配置',
    desc: '三级地区数据，每平台一份',
    platformScoped: true,
    getEndpoint: '/gateway/regions/Get',
    getMethod: 'GET',
    saveEndpoint: '/gateway/regions/Save',
    hint: '保存空字符串即可恢复内置默认库（内置只有省级，市 / 区县需要在这里导入完整数据）。',
    fields: [
      {
        field: 'regionsJson',
        label: '地区数据（JSON 数组）',
        type: 'textarea',
        rows: 18,
        placeholder: '[{ "code": 110000, "name": "北京市", "children": [] }]',
        help: '三级结构：省级 → 市 → 区县。code 用国标行政区划代码。',
        showBytes: true,
      },
    ],
  },

  // 优惠优先级。保存后必须调优惠快照失效（后端已做），否则改了不生效。
  promotionPriority: {
    title: '优惠优先级',
    desc: '同一单同时命中活动与券时，先算哪个',
    platformScoped: true,
    getEndpoint: '/gateway/marketing/marketing-config/Get',
    getMethod: 'GET',
    saveEndpoint: '/gateway/marketing/marketing-config/Save',
    hint: '默认「券优先」。',
    fields: [
      {
        field: 'priority',
        label: '优先级',
        type: 'select',
        options: [
          { value: 1, label: '活动优先（先取活动，无活动才取券）' },
          { value: 2, label: '券优先（先取最优券，无券才取活动）' },
        ],
      },
    ],
  },

  // 积分规则。7 条规则一起提交（整组覆盖），不是逐条增量改 ——
  // 增量改的话前端漏传一条会静默保留旧值，而运营以为自己已经改过了。
  pointRules: {
    title: '积分规则',
    desc: '改动只对之后的发放生效，已发放的积分不追溯',
    platformScoped: false,
    getEndpoint: '/gateway/points/Rules',
    getMethod: 'POST',
    saveEndpoint: '/gateway/points/SaveRules',
    hint: '余额上限、有效期、注册赠送、首评赠送、抵扣汇率、获取汇率、签到奖励档位。',
    fields: [
      { field: 'balanceCap', label: '余额上限（积分）', type: 'number' },
      { field: 'validDays', label: '有效期（天）', type: 'number' },
      { field: 'registerGift', label: '注册赠送（积分）', type: 'number' },
      { field: 'firstEvaluateGift', label: '首评赠送（积分）', type: 'number' },
      { field: 'pointsPerYuan', label: '抵扣汇率（多少积分抵 1 元）', type: 'number' },
      { field: 'earnPointsPerYuan', label: '获取汇率（1 元给多少积分）', type: 'number' },
      {
        field: 'signInRewards',
        label: '签到奖励档位（逗号分隔）',
        type: 'text',
        help: '第 N+1 天回到第 1 档。默认 1,2,3,5,8,10,15。',
        // 接口要整数数组，表单是逗号分隔文本。format / parse 互逆，都在配置里声明，
        // 组件不该知道「签到奖励」这种业务细节。
        format: (v: unknown) => (Array.isArray(v) ? v.join(',') : (v ?? '')),
        parse: (v: unknown) =>
          String(v ?? '')
            .split(',')
            .map((s) => s.trim())
            .filter((s) => s !== '')
            .map(Number),
      },
    ],
  },
};

export default CONFIGS;
